Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$terraformPath = Resolve-Path "$PSScriptRoot\..\..\pipelines\terraform-function\azurerm_function_app.tf"
$terraform = Get-Content -LiteralPath $terraformPath -Raw
$inputsPath = Resolve-Path "$PSScriptRoot\..\..\pipelines\terraform-function\inputs.tf"
$inputs = Get-Content -LiteralPath $inputsPath -Raw

$requiredExpressions = @(
    'length\(var\.cost_export_cutover_subscription_ids\)\s*==\s*0\s*\|\|\s*var\.enable_cost_export_import',
    'var\.cost_export_import_all_subscriptions\s*\|\|\s*\(\s*length\(setsubtract\(var\.cost_export_cutover_subscription_ids,\s*var\.cost_export_subscription_ids\)\)\s*==\s*0'
)
foreach ($expression in $requiredExpressions) {
    if ($terraform -notmatch $expression) {
        throw "Precondition Terraform ausente ou alterada: $expression"
    }
}

function Test-CutoverConfiguration {
    param(
        [Parameter(Mandatory)][bool]$ImportEnabled,
        [Parameter(Mandatory)][bool]$ImportAll,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Ingestion,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Cutover
    )

    if ($Cutover.Count -gt 0 -and -not $ImportEnabled) {
        return $false
    }

    if ($ImportAll) {
        return $true
    }

    return @($Cutover | Where-Object { $_ -notin $Ingestion }).Count -eq 0
}

if (Test-CutoverConfiguration -ImportEnabled $false -ImportAll $true -Ingestion @() -Cutover @("sub-a")) {
    throw "Cutover com importação desabilitada deveria falhar no plan."
}
if (Test-CutoverConfiguration -ImportEnabled $true -ImportAll $false -Ingestion @("sub-a") -Cutover @("sub-b")) {
    throw "Cutover fora da ingestão deveria falhar no plan."
}
if (-not (Test-CutoverConfiguration -ImportEnabled $true -ImportAll $false -Ingestion @("sub-a") -Cutover @("sub-a"))) {
    throw "Cutover válido deveria ser aceito."
}
if (-not (Test-CutoverConfiguration -ImportEnabled $true -ImportAll $true -Ingestion @() -Cutover @("sub-any"))) {
    throw "Import-all deveria aceitar cutover explícito sem repetir a allowlist."
}
if (-not (Test-CutoverConfiguration -ImportEnabled $false -ImportAll $false -Ingestion @() -Cutover @())) {
    throw "Configuração padrão vazia deveria ser aceita."
}

foreach ($enabledByDefaultVariable in @(
        "enable_cost_export_import",
        "cost_export_import_all_subscriptions")) {
    $pattern = '(?s)variable\s+"' + [regex]::Escape($enabledByDefaultVariable) + '"\s*\{.+?default\s*=\s*true'
    if ($inputs -notmatch $pattern) {
        throw "A variável '$enabledByDefaultVariable' deve estar habilitada por padrão."
    }
}

Write-Host "Validação offline das preconditions Terraform de cutover concluída com sucesso."
