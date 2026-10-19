Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Resolve-Path "$PSScriptRoot\..\.."
$inputs = Get-Content -LiteralPath "$root\pipelines\terraform-function\inputs.tf" -Raw
$functionApp = Get-Content -LiteralPath "$root\pipelines\terraform-function\azurerm_function_app.tf" -Raw
$serviceBus = Get-Content -LiteralPath "$root\pipelines\terraform-function\azurerm_service_bus.tf" -Raw
$localSettings = Get-Content -LiteralPath "$root\src\Personal.FinOpsApi.AzureFunctions\local.settings.sample.json" -Raw | ConvertFrom-Json
$queueService = Get-Content -LiteralPath "$root\src\Personal.FinOpsApi.AzureFunctions\Services\QueueService.cs" -Raw

foreach ($variable in @(
        "enable_legacy_cost_by_service_collection",
        "enable_legacy_cost_by_resource_collection")) {
    $pattern = '(?s)variable\s+"' + [regex]::Escape($variable) + '"\s*\{.+?default\s*=\s*false'
    if ($inputs -notmatch $pattern) {
        throw "A variável '$variable' deve permanecer desabilitada por padrão."
    }
}

foreach ($setting in @(
        "EnableLegacyCostByServiceCollection",
        "EnableLegacyCostByResourceCollection")) {
    if ($localSettings.Values.$setting -ne "false") {
        throw "A configuração local '$setting' deve permanecer desabilitada por padrão."
    }
}

$removedQueues = @{
    storage_analysis   = "storage-analysis"
    vm_analysis        = "vm-analysis"
    appservice_analysis = "appservice-analysis"
    analysis_results   = "analysis-results"
}

foreach ($entry in $removedQueues.GetEnumerator()) {
    $resourcePattern = 'resource\s+"azurerm_servicebus_queue"\s+"' + [regex]::Escape($entry.Key) + '"'
    if ($serviceBus -match $resourcePattern) {
        throw "Fila obsoleta ainda provisionada: $($entry.Value)"
    }

    if ($queueService -match ('"' + [regex]::Escape($entry.Value) + '"')) {
        throw "QueueService ainda referencia a fila obsoleta: $($entry.Value)"
    }
}

foreach ($setting in @(
        "QUEUE_STORAGE_ANALYSIS",
        "QUEUE_VM_ANALYSIS",
        "QUEUE_APPSERVICE_ANALYSIS",
        "QUEUE_ANALYSIS_RESULTS")) {
    if ($functionApp -match ('"' + [regex]::Escape($setting) + '"\s*=')) {
        throw "App Setting de fila obsoleta ainda configurada: $setting"
    }
}

$storageWorker = Join-Path $root "src\Personal.FinOpsApi.AzureFunctions\Functions\StorageAnalysisQueueFunction.cs"
if (Test-Path -LiteralPath $storageWorker) {
    throw "O worker da fila storage-analysis deveria ter sido removido."
}

Write-Host "Validação offline dos coletores e filas legadas concluída com sucesso."
