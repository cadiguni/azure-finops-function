Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptPath = Resolve-Path "$PSScriptRoot\..\..\scripts\Initialize-CostExports.ps1"
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath,
    [ref]$tokens,
    [ref]$parseErrors)

if ($parseErrors.Count -ne 0) {
    throw "O script contém erros de sintaxe: $($parseErrors | Out-String)"
}

foreach ($functionName in @("Format-UtcTimestamp", "Assert-ExportScheduleCombination", "New-ExportBody")) {
    $functionAst = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $functionName
        }, $true)
    if ($null -eq $functionAst) {
        throw "Função '$functionName' não encontrada."
    }

    Invoke-Expression $functionAst.Extent.Text
}

$script:CompressionMode = "gzip"
$script:PartitionData = $true
$script:ContainerName = "cost-exports"
$body = New-ExportBody `
    -ExportType "Usage" `
    -Timeframe "MonthToDate" `
    -Recurrence "Daily" `
    -RootFolderPath "subscriptions/00000000-0000-0000-0000-000000000001/billing-period/current" `
    -StorageResourceId "/subscriptions/storage/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/account" `
    -ManagedIdentityLocation "brazilsouth" `
    -ScheduleTo "2030-01-01T00:00:00Z"

if ($body.properties.definition.type -ne "Usage") {
    throw "O export principal deve usar Usage."
}
if ($body.properties.definition.timeframe -ne "MonthToDate") {
    throw "O export principal deve usar MonthToDate."
}
if ($body.properties.definition.dataSet.granularity -ne "Daily") {
    throw "O export principal deve usar granularidade Daily."
}
if ($body.properties.schedule.recurrence -ne "Daily") {
    throw "O export principal deve usar recorrência Daily."
}
if ($body.properties.definition.dataSet.ContainsKey("configuration")) {
    throw "Usage não deve declarar dataVersion."
}

$lastBody = New-ExportBody `
    -ExportType "Usage" `
    -Timeframe "TheLastBillingMonth" `
    -Recurrence "Monthly" `
    -RootFolderPath "subscriptions/00000000-0000-0000-0000-000000000001/billing-period/last" `
    -StorageResourceId "/subscriptions/storage/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/account" `
    -ManagedIdentityLocation "brazilsouth" `
    -ScheduleTo "2030-01-01T00:00:00Z"

if ($lastBody.properties.definition.timeframe -ne "TheLastBillingMonth" -or
    $lastBody.properties.schedule.recurrence -ne "Monthly") {
    throw "O export do último ciclo deve usar TheLastBillingMonth + Monthly."
}

$invalidCombinationRejected = $false
try {
    New-ExportBody `
        -ExportType "Usage" `
        -Timeframe "BillingMonthToDate" `
        -Recurrence "Daily" `
        -RootFolderPath "subscriptions/00000000-0000-0000-0000-000000000001/billing-period/current" `
        -StorageResourceId "/subscriptions/storage/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/account" `
        -ManagedIdentityLocation "brazilsouth" `
        -ScheduleTo "2030-01-01T00:00:00Z" | Out-Null
}
catch {
    $invalidCombinationRejected = $_.Exception.Message -match 'MonthToDate \+ Daily'
}
if (-not $invalidCombinationRejected) {
    throw "BillingMonthToDate + Daily deve ser rejeitado localmente."
}

$scriptText = Get-Content -LiteralPath $scriptPath -Raw
if ($scriptText -notmatch '\[string\]\$ExportType\s*=\s*"Usage"') {
    throw "O valor padrão de ExportType deve ser Usage."
}
if ($scriptText -notmatch 'Name\s*=\s*\$ExportName\s+Timeframe\s*=\s*"MonthToDate"\s+Recurrence\s*=\s*"Daily"') {
    throw "A definição do export atual deve usar MonthToDate + Daily."
}
if ($scriptText -notmatch 'Timeframe\s*=\s*"TheLastBillingMonth"\s+Recurrence\s*=\s*"Monthly"') {
    throw "O export opcional anterior deve usar TheLastBillingMonth + Monthly."
}
if ($scriptText -match '"Auto"') {
    throw "O modo Auto não deve permanecer habilitado implicitamente."
}

Write-Host "Validação offline do corpo de Cost Management Export concluída com sucesso."
