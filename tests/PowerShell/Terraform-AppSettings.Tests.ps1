Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$terraformPath = Resolve-Path "$PSScriptRoot\..\..\pipelines\terraform-function\azurerm_function_app.tf"
$terraform = Get-Content -LiteralPath $terraformPath -Raw
$localSettingsPath = Resolve-Path "$PSScriptRoot\..\..\src\Personal.FinOpsApi.AzureFunctions\local.settings.sample.json"
$localSettings = Get-Content -LiteralPath $localSettingsPath -Raw

if ($terraform -notmatch '"AZURE_MANAGEMENT_GROUP_ID"\s*=\s*var\.root_management_group') {
    throw "AZURE_MANAGEMENT_GROUP_ID deve ser configurado por var.root_management_group."
}

$removedSettings = @(
    "FinOps__SubscriptionId",
    "FinOps__TenantId",
    "FinOps__StorageAccountName",
    "EnableSqlDatabaseAnalyzer",
    "EnableCosmosDatabaseAnalyzer",
    "StorageProvider",
    "AzureFunctionsJobHost__functionTimeout",
    "AZURE_MONITOR_MAX_RETRIES",
    "AZURE_MONITOR_TIMEOUT_SECONDS",
    "STORAGE_ACCOUNT_MAX_ANALYSIS",
    "SERVICEBUS_NAMESPACE"
)

foreach ($setting in $removedSettings) {
    $pattern = '"' + [regex]::Escape($setting) + '"\s*='
    if ($terraform -match $pattern) {
        throw "App Setting obsoleta voltou ao Terraform: $setting"
    }

    $jsonPattern = '"' + [regex]::Escape($setting) + '"\s*:'
    if ($localSettings -match $jsonPattern) {
        throw "App Setting obsoleta voltou ao local.settings.sample.json: $setting"
    }
}

Write-Host "Validação offline das App Settings Terraform concluída com sucesso."
