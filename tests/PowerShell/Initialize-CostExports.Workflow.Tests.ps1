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
    throw "Initialize-CostExports.ps1 contém erros de sintaxe: $($parseErrors -join '; ')"
}

$scriptText = Get-Content -LiteralPath $scriptPath -Raw
$requiredPatterns = [ordered]@{
    'eTag existente preservado com -Force' =
        '\$body\.eTag\s*=\s*\[string\]\$existingExport\.eTag'
    'eTag atualizado usado na ativação' =
        '\$body\.eTag\s*=\s*\[string\]\$configuredExport\.eTag'
    'ativação ocorre antes do RunNow' =
        '(?s)\$activatedExport\s*=\s*Invoke-AzRestJson.+?\$summary\.Activated\+\+.+?if\s*\(\$RunNow\).+?-Method\s+post'
}

foreach ($requirement in $requiredPatterns.GetEnumerator()) {
    if ($scriptText -notmatch $requirement.Value) {
        throw "Fluxo seguro ausente ou alterado: $($requirement.Key)."
    }
}

Write-Host "Validação offline do fluxo seguro de atualização, ativação e RunNow concluída com sucesso."
