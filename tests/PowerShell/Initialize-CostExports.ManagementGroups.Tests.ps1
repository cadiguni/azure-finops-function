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

$functionAst = $ast.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq "Get-ManagementGroupSubscriptionIds"
    },
    $true)

if ($null -eq $functionAst) {
    throw "Função Get-ManagementGroupSubscriptionIds não encontrada."
}

Invoke-Expression $functionAst.Extent.Text

$firstSubscription = "00000000-0000-0000-0000-00000000a006"
$secondSubscription = "00000000-0000-0000-0000-00000000a007"
$script:responses = [System.Collections.Generic.Queue[object]]::new()
$script:responses.Enqueue([pscustomobject]@{
    value = @(
        [pscustomobject]@{
            type = "Microsoft.Management/managementGroups"
            name = "child-management-group"
            properties = [pscustomobject]@{ displayName = "Child" }
        },
        [pscustomobject]@{
            type = "Microsoft.Management/managementGroups/subscriptions"
            name = $firstSubscription
        },
        [pscustomobject]@{
            type = "Microsoft.Management/managementGroups/subscriptions"
            name = $firstSubscription
        }
    )
    nextLink = "https://management.azure.com/next-page"
})
$script:responses.Enqueue([pscustomobject]@{
    value = @(
        [pscustomobject]@{
            id = "/providers/Microsoft.Management/managementGroups/root/subscriptions/$secondSubscription"
            properties = [pscustomobject]@{ resourceType = "Microsoft.Resources/subscriptions" }
        },
        [pscustomobject]@{
            type = "Microsoft.Management/managementGroups"
            properties = $null
        }
    )
})

function Invoke-AzRestJson {
    param(
        [string]$Method,
        [string]$Uri,
        [string]$Operation,
        [string]$SubscriptionId
    )

    if ($script:responses.Count -eq 0) {
        throw "A função tentou consultar uma página não preparada pelo teste offline."
    }

    return $script:responses.Dequeue()
}

$actual = @(Get-ManagementGroupSubscriptionIds -GroupId "root")
$expected = @($firstSubscription, $secondSubscription) | Sort-Object

$differences = @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual)
if ($actual.Count -ne $expected.Count -or $differences.Count -ne 0) {
    throw "IDs inesperados. Esperado='$($expected -join ',')'; atual='$($actual -join ',')'."
}

if ($script:responses.Count -ne 0) {
    throw "A paginação não consumiu todas as respostas offline."
}

Write-Host "Validação offline de Management Groups concluída com sucesso."
