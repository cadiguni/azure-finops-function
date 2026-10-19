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

foreach ($functionName in @("Get-AccessibleSubscription", "Test-CostExportSubscriptionAccess")) {
    $functionAst = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $functionName
        }, $true)
    if ($null -eq $functionAst) {
        throw "Função $functionName não encontrada."
    }
    Invoke-Expression $functionAst.Extent.Text
}

$ApiVersion = "2025-03-01"
$tenantId = "tenant-a"
$script:subscriptions = @{}

function Reset-TestSummary {
    $script:summary = [ordered]@{
        EnabledSubscriptionsProcessed = 0
        IneligibleSubscriptionsSkipped = 0
        InaccessibleSubscriptions = 0
        Failures = [System.Collections.Generic.List[string]]::new()
    }
}

function Invoke-AzCliWithRetry {
    param([string[]]$Arguments, [string]$Operation, [string]$SubscriptionId)
    if (-not $script:subscriptions.ContainsKey($SubscriptionId)) {
        throw "Assinatura inacessível '$SubscriptionId'."
    }
    return [pscustomobject]@{
        Output = @($script:subscriptions[$SubscriptionId] | ConvertTo-Json -Compress)
    }
}

function Invoke-AzRestJson {
    param([string]$Method, [string]$Uri, [string]$Operation, [string]$SubscriptionId)
    return [pscustomobject]@{ value = @() }
}

function Assert-Equal {
    param($Actual, $Expected, [string]$Message)
    if ($Actual -ne $Expected) {
        throw "$Message Esperado='$Expected'; atual='$Actual'."
    }
}

# 1. -AllSubscriptions com Enabled e Disabled.
Reset-TestSummary
$script:subscriptions = @{
    enabled = [pscustomobject]@{ id = "enabled"; tenantId = $tenantId; state = "Enabled" }
    disabled = [pscustomobject]@{ id = "disabled"; tenantId = $tenantId; state = "Disabled" }
}
$enabledAccepted = Test-CostExportSubscriptionAccess -SubscriptionId enabled -CurrentTenantId $tenantId -DiscoveredByAllSubscriptions $true
$disabledAccepted = Test-CostExportSubscriptionAccess -SubscriptionId disabled -CurrentTenantId $tenantId -DiscoveredByAllSubscriptions $true
Assert-Equal $enabledAccepted $true "A assinatura habilitada deveria ser aceita."
Assert-Equal $disabledAccepted $false "A assinatura desativada descoberta deveria ser ignorada."
Assert-Equal $summary.EnabledSubscriptionsProcessed 1 "Contador de habilitadas incorreto."
Assert-Equal $summary.IneligibleSubscriptionsSkipped 1 "Contador de inelegíveis incorreto."
Assert-Equal $summary.Failures.Count 0 "Subscription descoberta e desativada não deve falhar."

# 2. -SubscriptionIds explícito com Disabled.
Reset-TestSummary
$explicitAccepted = Test-CostExportSubscriptionAccess -SubscriptionId disabled -CurrentTenantId $tenantId -DiscoveredByAllSubscriptions $false
Assert-Equal $explicitAccepted $false "Subscription explicitamente solicitada e desativada deveria falhar."
Assert-Equal $summary.IneligibleSubscriptionsSkipped 0 "Subscription explícita não deve ser contabilizada como ignorada."
Assert-Equal $summary.Failures.Count 1 "Subscription explícita desativada deve registrar falha."

# 3. Management Group contendo somente subscriptions desativadas.
Reset-TestSummary
$disabledAccepted = Test-CostExportSubscriptionAccess -SubscriptionId disabled -CurrentTenantId $tenantId -DiscoveredByAllSubscriptions $true
Assert-Equal $disabledAccepted $false "Subscription desativada deveria ser ignorada."
Assert-Equal $summary.EnabledSubscriptionsProcessed 0 "Nenhuma habilitada deveria ser processada."
Assert-Equal $summary.IneligibleSubscriptionsSkipped 1 "A inelegível deveria ser contabilizada."
Assert-Equal $summary.Failures.Count 0 "Somente inelegíveis ignoradas não podem causar falha."

# 4. Subscription inacessível ou em outro tenant continua falha real.
Reset-TestSummary
$script:subscriptions.otherTenant = [pscustomobject]@{ id = "otherTenant"; tenantId = "tenant-b"; state = "Enabled" }
$null = Test-CostExportSubscriptionAccess -SubscriptionId unavailable -CurrentTenantId $tenantId -DiscoveredByAllSubscriptions $true
$null = Test-CostExportSubscriptionAccess -SubscriptionId otherTenant -CurrentTenantId $tenantId -DiscoveredByAllSubscriptions $true
Assert-Equal $summary.InaccessibleSubscriptions 2 "Falhas de acesso/tenant devem ser contabilizadas como inacessíveis."
Assert-Equal $summary.Failures.Count 2 "Falhas de acesso/tenant devem permanecer falhas reais."

Write-Host "Validação offline de elegibilidade de subscriptions concluída com sucesso."
