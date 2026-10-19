[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "High")]
param(
    [string]$ManagementGroupId,

    [Parameter(Mandatory)]
    [string]$StorageSubscriptionId,

    [Parameter(Mandatory)]
    [string]$StorageResourceGroup,

    [Parameter(Mandatory)]
    [string]$StorageAccountName,

    [string]$ContainerName = "cost-exports",
    [string]$ExportName = "finops-actual-cost-current-billing",
    [string]$ApiVersion = "2025-03-01",
    [string]$Location,
    [DateTimeOffset]$ScheduleEndUtc = [DateTimeOffset]::UtcNow.AddYears(5),

    [ValidateSet("gzip", "none")]
    [string]$CompressionMode = "gzip",

    [ValidateSet("Usage", "ActualCost", "AmortizedCost")]
    [string]$ExportType = "Usage",

    [ValidateScript({
            if (-not $_) {
                throw "PartitionData=false não é suportado pelo Cost Management Export. Omita o parâmetro ou use -PartitionData:`$true."
            }
            return $true
        })]
    [bool]$PartitionData = $true,
    [string[]]$SubscriptionIds,
    [switch]$AllSubscriptions,
    [switch]$IncludeLastBillingPeriod,
    [switch]$Activate,
    [switch]$RunNow,
    [switch]$Force,

    [ValidateRange(1, 10)]
    [int]$MaxRetryAttempts = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$azureCliVerbose = $PSBoundParameters.ContainsKey("Verbose") -and
    [bool]$PSBoundParameters["Verbose"]

$storageApiVersion = "2023-05-01"
# Nomes preservados para atualizar os exports existentes de forma idempotente.
$lastBillingExportName = "finops-actual-cost-last-billing"
$storageBlobDataContributorRoleName = "Storage Blob Data Contributor"
$storageBlobDataContributorRoleId = "ba92f5b4-2d11-453d-a403-e96b0029c9fe"
$rbacInitialPropagationDelaySeconds = 10
$failedIdentityRetryDelaysSeconds = @(15, 30, 60, 120)
$summary = [ordered]@{
    Created = 0
    Updated = 0
    Skipped = 0
    Activated = 0
    RunRequested = 0
    EnabledSubscriptionsProcessed = 0
    IneligibleSubscriptionsSkipped = 0
    InaccessibleSubscriptions = 0
    ExportTypes = [ordered]@{}
    Failures = [System.Collections.Generic.List[string]]::new()
}

function Get-HttpStatusCode {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$ErrorText
    )

    if ([string]::IsNullOrWhiteSpace($ErrorText)) {
        return $null
    }

    $patterns = @(
        '(?im)\b(?:response\s+status|status(?:\s+code)?)\s*[:=]\s*(?<code>[1-5]\d{2})\b',
        '(?im)\bHTTP(?:/\d(?:\.\d)?)?\s+(?<code>[1-5]\d{2})\b',
        '(?im)["'']code["'']\s*:\s*["'']?(?<code>[1-5]\d{2})\b'
    )

    foreach ($pattern in $patterns) {
        $match = [regex]::Match($ErrorText, $pattern)
        if ($match.Success) {
            return [int]$match.Groups["code"].Value
        }
    }

    $statusPhrases = [ordered]@{
        '(?im)\bToo Many Requests\b' = 429
        '(?im)\bRequest Timeout\b' = 408
        '(?im)\bGateway Timeout\b' = 504
        '(?im)\bService Unavailable\b' = 503
        '(?im)\bBad Gateway\b' = 502
        '(?im)\bInternal Server Error\b' = 500
        '(?im)\bNot Found\b' = 404
        '(?im)\bForbidden\b' = 403
        '(?im)\bUnauthorized\b' = 401
        '(?im)\bBad Request\b' = 400
    }
    foreach ($entry in $statusPhrases.GetEnumerator()) {
        if ($ErrorText -match $entry.Key) {
            return [int]$entry.Value
        }
    }

    return $null
}

function Get-RetryAfterSeconds {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$ErrorText
    )

    if ([string]::IsNullOrWhiteSpace($ErrorText)) {
        return $null
    }

    $millisecondsMatch = [regex]::Match(
        $ErrorText,
        '(?im)\bx-ms-retry-after-ms\b[^0-9]*(?<value>\d+)')
    if ($millisecondsMatch.Success) {
        return [Math]::Max(
            1,
            [Math]::Ceiling([double]$millisecondsMatch.Groups["value"].Value / 1000))
    }

    $secondsMatch = [regex]::Match(
        $ErrorText,
        '(?im)\bretry-after\b["'']?\s*[:=]\s*["'']?(?<value>\d+)\b')
    if ($secondsMatch.Success) {
        return [Math]::Max(1, [double]$secondsMatch.Groups["value"].Value)
    }

    $dateMatch = [regex]::Match(
        $ErrorText,
        '(?im)\bretry-after\b["'']?\s*[:=]\s*["'']?(?<value>[A-Za-z]{3},[^\r\n"'']+)')
    if ($dateMatch.Success) {
        $retryAt = [DateTimeOffset]::MinValue
        if ([DateTimeOffset]::TryParse(
                $dateMatch.Groups["value"].Value.Trim(),
                [ref]$retryAt)) {
            return [Math]::Max(
                1,
                [Math]::Ceiling(($retryAt - [DateTimeOffset]::UtcNow).TotalSeconds))
        }
    }

    return $null
}

function New-AzCliException {
    param(
        [Parameter(Mandatory)][string]$Message,
        [AllowNull()][Nullable[int]]$StatusCode,
        [AllowEmptyString()][string]$ErrorCode,
        [AllowEmptyString()][string]$ErrorMessage
    )

    $exception = [InvalidOperationException]::new($Message)
    if ($null -ne $StatusCode) {
        $exception.Data["StatusCode"] = [int]$StatusCode
    }
    $exception.Data["ErrorCode"] = $ErrorCode
    $exception.Data["ErrorMessage"] = $ErrorMessage
    return $exception
}

function ConvertFrom-JsonEscapedText {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    try {
        return ('"' + $Value + '"') | ConvertFrom-Json
    }
    catch {
        return $Value
    }
}

function Remove-SensitiveErrorData {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    $sanitized = $Value -replace '(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*', 'Bearer [REDACTED]'
    $sanitized = $sanitized -replace '(?i)\b(Authorization|access_token|refresh_token|id_token)\b\s*[:=]\s*["'']?[^"'',;\s]+', '$1=[REDACTED]'
    return ($sanitized -replace '\s+', ' ').Trim()
}

function Get-AzCliErrorDetails {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$ErrorText,

        [Parameter(Mandatory)]
        [int]$ExitCode
    )

    $statusCode = Get-HttpStatusCode -ErrorText $ErrorText
    $errorCode = ""
    $errorMessage = ""
    $armErrorMatch = [regex]::Match(
        $ErrorText,
        '(?is)"error"\s*:\s*\{\s*"code"\s*:\s*"(?<code>(?:\\.|[^"])*)"\s*,\s*"message"\s*:\s*"(?<message>(?:\\.|[^"])*)"')

    if ($armErrorMatch.Success) {
        $errorCode = ConvertFrom-JsonEscapedText -Value $armErrorMatch.Groups["code"].Value
        $errorMessage = ConvertFrom-JsonEscapedText -Value $armErrorMatch.Groups["message"].Value
    } else {
        $cliErrorMatch = [regex]::Match(
            $ErrorText,
            '(?im)^\s*ERROR:\s*(?:\((?<code>[^)]+)\)\s*)?(?<message>[^\r\n]+)')
        if ($cliErrorMatch.Success) {
            $errorCode = $cliErrorMatch.Groups["code"].Value
            $errorMessage = $cliErrorMatch.Groups["message"].Value
        }
    }

    if ([string]::IsNullOrWhiteSpace($errorCode)) {
        $errorCode = if ($null -ne $statusCode) {
            [string]$statusCode
        } else {
            "AzureCliExitCode$ExitCode"
        }
    }
    if ([string]::IsNullOrWhiteSpace($errorMessage)) {
        $errorMessage = "Azure CLI encerrou com código $ExitCode sem detalhes estruturados do ARM."
    }

    return [pscustomobject]@{
        StatusCode = $statusCode
        ErrorCode = Remove-SensitiveErrorData -Value ([string]$errorCode)
        ErrorMessage = Remove-SensitiveErrorData -Value ([string]$errorMessage)
    }
}

function Format-AzCliErrorDetails {
    param(
        [AllowNull()][Nullable[int]]$StatusCode,
        [Parameter(Mandatory)][string]$ErrorCode,
        [Parameter(Mandatory)][string]$ErrorMessage
    )

    $httpStatus = if ($null -ne $StatusCode) {
        [string]$StatusCode
    } else {
        "indisponível"
    }
    return "HTTP status: $httpStatus; error.code: $ErrorCode; error.message: $ErrorMessage"
}

function Invoke-AzCliWithRetry {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$Operation,

        [string]$SubscriptionId = "n/a",
        [switch]$RetryOnPrincipalReplication,
        [switch]$RetryOnFailedIdentityOperation,
        [switch]$AcceptRoleAssignmentExists
    )

    for ($attempt = 1; $attempt -le $MaxRetryAttempts; $attempt++) {
        $errorFile = Join-Path (
            [System.IO.Path]::GetTempPath()
        ) "finops-az-cli-$([Guid]::NewGuid().ToString('N')).log"
        $previousWhatIfPreference = $WhatIfPreference

        try {
            # Redirection is implemented by Out-File, which otherwise inherits -WhatIf
            # and skips capturing Azure CLI errors needed to distinguish a real 404.
            $WhatIfPreference = $false
            $cliArguments = [System.Collections.Generic.List[string]]::new()
            $cliArguments.AddRange([string[]]$Arguments)
            if ($azureCliVerbose) {
                $cliArguments.Add("--verbose")
            }
            $output = @(& az @cliArguments 2> $errorFile)
            $exitCode = $LASTEXITCODE
            $errorText = if ([System.IO.File]::Exists($errorFile)) {
                [System.IO.File]::ReadAllText($errorFile)
            } else {
                ""
            }
        }
        finally {
            $WhatIfPreference = $previousWhatIfPreference
            if ([System.IO.File]::Exists($errorFile)) {
                [System.IO.File]::Delete($errorFile)
            }
        }

        if ($exitCode -eq 0) {
            return [pscustomobject]@{
                Output = $output
                ErrorText = ""
            }
        }

        $combinedError = (@($errorText, ($output -join [Environment]::NewLine)) |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join [Environment]::NewLine
        $errorDetails = Get-AzCliErrorDetails `
            -ErrorText $combinedError `
            -ExitCode $exitCode
        $statusCode = $errorDetails.StatusCode
        $isTransientHttpError = $statusCode -eq 408 -or
            $statusCode -eq 429 -or
            ($null -ne $statusCode -and $statusCode -ge 500 -and $statusCode -le 599)
        $isPrincipalReplicationError = $RetryOnPrincipalReplication -and
            (@($errorDetails.ErrorCode, $errorDetails.ErrorMessage) -join " ") -match '(?i)PrincipalNotFound|IdentityNotFound|Cannot find user or service principal|principal.+(?:does not exist|not found)'
        $isFailedIdentityOperation = $RetryOnFailedIdentityOperation -and
            $errorDetails.ErrorCode -eq "FailedIdentityOperation"
        $isExistingRoleAssignment = $AcceptRoleAssignmentExists -and
            (@($errorDetails.ErrorCode, $errorDetails.ErrorMessage) -join " ") -match '(?i)RoleAssignmentExists|role assignment already exists'

        if ($isExistingRoleAssignment) {
            return [pscustomobject]@{
                Output = @()
                ErrorText = ""
            }
        }

        $formattedError = Format-AzCliErrorDetails `
            -StatusCode $statusCode `
            -ErrorCode $errorDetails.ErrorCode `
            -ErrorMessage $errorDetails.ErrorMessage
        $effectiveMaxAttempts = if ($isFailedIdentityOperation) {
            [Math]::Min(
                $MaxRetryAttempts,
                $failedIdentityRetryDelaysSeconds.Count + 1)
        } else {
            $MaxRetryAttempts
        }
        if (-not $isTransientHttpError -and
            -not $isPrincipalReplicationError -and
            -not $isFailedIdentityOperation) {
            throw (New-AzCliException `
                    -Message "Falha permanente em '$Operation' para a assinatura '$SubscriptionId'. $formattedError" `
                    -StatusCode $statusCode `
                    -ErrorCode $errorDetails.ErrorCode `
                    -ErrorMessage $errorDetails.ErrorMessage)
        }

        if ($attempt -ge $effectiveMaxAttempts) {
            throw (New-AzCliException `
                    -Message "Falha transitória persistente em '$Operation' para a assinatura '$SubscriptionId' após $attempt tentativa(s). $formattedError" `
                    -StatusCode $statusCode `
                    -ErrorCode $errorDetails.ErrorCode `
                    -ErrorMessage $errorDetails.ErrorMessage)
        }

        $retryAfterSeconds = if ($statusCode -eq 429) {
            Get-RetryAfterSeconds -ErrorText $combinedError
        } else {
            $null
        }
        $baseDelaySeconds = [Math]::Min(60, [Math]::Pow(2, $attempt))
        $delaySeconds = if ($isFailedIdentityOperation) {
            $delayIndex = [Math]::Min(
                $attempt - 1,
                $failedIdentityRetryDelaysSeconds.Count - 1)
            $failedIdentityRetryDelaysSeconds[$delayIndex]
        } elseif ($null -ne $retryAfterSeconds) {
            [double]$retryAfterSeconds
        } else {
            $baseDelaySeconds + (Get-Random -Minimum 0 -Maximum 1000) / 1000
        }

        Write-Warning (
            "Tentativa {0}/{1} falhou em '{2}' para a assinatura '{3}'. " +
            "Nova tentativa em {4:N1}s." -f
            $attempt,
            $effectiveMaxAttempts,
            $Operation,
            $SubscriptionId,
            $delaySeconds)
        Start-Sleep -Seconds $delaySeconds
    }
}

function Invoke-AzRestJson {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("get", "put", "post")]
        [string]$Method,

        [Parameter(Mandatory)]
        [string]$Uri,

        [object]$Body,

        [Parameter(Mandatory)]
        [string]$Operation,

        [string]$SubscriptionId = "n/a",
        [switch]$RetryOnFailedIdentityOperation
    )

    $bodyFile = $null
    try {
        $arguments = @("rest", "--method", $Method, "--uri", $Uri, "--output", "json")
        if ($null -ne $Body) {
            $bodyFile = Join-Path (
                [System.IO.Path]::GetTempPath()
            ) "finops-az-rest-$([Guid]::NewGuid().ToString('N')).json"
            $json = $Body | ConvertTo-Json -Depth 20 -Compress
            [System.IO.File]::WriteAllText(
                $bodyFile,
                $json,
                [System.Text.UTF8Encoding]::new($false))
            $arguments += @(
                "--headers",
                "Content-Type=application/json",
                "--body",
                "@$bodyFile"
            )
        }

        $result = Invoke-AzCliWithRetry `
            -Arguments $arguments `
            -Operation $Operation `
            -SubscriptionId $SubscriptionId `
            -RetryOnFailedIdentityOperation:$RetryOnFailedIdentityOperation
    }
    finally {
        if ($null -ne $bodyFile -and [System.IO.File]::Exists($bodyFile)) {
            [System.IO.File]::Delete($bodyFile)
        }
    }

    $outputText = $result.Output -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($outputText)) {
        return $null
    }

    return $outputText | ConvertFrom-Json
}

function Get-AzResourceIfExists {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$Operation,
        [string]$SubscriptionId = "n/a"
    )

    try {
        return Invoke-AzRestJson `
            -Method get `
            -Uri $Uri `
            -Operation $Operation `
            -SubscriptionId $SubscriptionId
    }
    catch {
        $statusCode = $_.Exception.Data["StatusCode"]
        if ($null -ne $statusCode -and [int]$statusCode -eq 404) {
            return $null
        }

        throw "Não foi possível verificar o recurso em '$Uri'. A resposta não foi 404 e não pode ser tratada como recurso inexistente. $($_.Exception.Message)"
    }
}

function Get-ManagementGroupSubscriptionIds {
    param([Parameter(Mandatory)][string]$GroupId)

    $uri = "https://management.azure.com/providers/Microsoft.Management/managementGroups/$GroupId/descendants?api-version=2020-05-01"
    $ids = [System.Collections.Generic.List[string]]::new()

    while (-not [string]::IsNullOrWhiteSpace($uri)) {
        $response = Invoke-AzRestJson `
            -Method get `
            -Uri $uri `
            -Operation "listar assinaturas do Management Group" `
            -SubscriptionId "management-group:$GroupId"

        if ($response.PSObject.Properties.Name -notcontains "value") {
            throw "A API de Management Groups não retornou a propriedade 'value' para o grupo '$GroupId'."
        }

        foreach ($item in @($response.value)) {
            $itemType = if ($item.PSObject.Properties.Name -contains "type") {
                [string]$item.type
            } else {
                ""
            }

            $resourceType = ""
            if ($item.PSObject.Properties.Name -contains "properties" -and
                $null -ne $item.properties -and
                $item.properties.PSObject.Properties.Name -contains "resourceType") {
                $resourceType = [string]$item.properties.resourceType
            }

            $isSubscription =
                $itemType -eq "Microsoft.Management/managementGroups/subscriptions" -or
                $itemType -eq "Microsoft.Resources/subscriptions" -or
                $itemType -eq "/subscriptions" -or
                $resourceType -eq "Microsoft.Resources/subscriptions" -or
                $resourceType -eq "/subscriptions"

            if (-not $isSubscription) {
                continue
            }

            $subscriptionId = if ($item.PSObject.Properties.Name -contains "name") {
                [string]$item.name
            } else {
                ""
            }

            if ([string]::IsNullOrWhiteSpace($subscriptionId) -and
                $item.PSObject.Properties.Name -contains "id" -and
                [string]$item.id -match "/subscriptions/(?<subscriptionId>[^/]+)$") {
                $subscriptionId = $Matches["subscriptionId"]
            }

            $parsedSubscriptionId = [Guid]::Empty
            if (-not [Guid]::TryParse($subscriptionId, [ref]$parsedSubscriptionId)) {
                Write-Warning "Item de subscription ignorado porque não contém um ID válido. type='$itemType', name='$subscriptionId'."
                continue
            }

            $ids.Add($parsedSubscriptionId.ToString())
        }

        $uri = if ($response.PSObject.Properties.Name -contains "nextLink" -and
            -not [string]::IsNullOrWhiteSpace([string]$response.nextLink)) {
            [string]$response.nextLink
        } else {
            $null
        }
    }

    return $ids | Sort-Object -Unique
}

function Get-AccessibleSubscription {
    param(
        [Parameter(Mandatory)][string]$SubscriptionId,
        [Parameter(Mandatory)][string]$CurrentTenantId,
        [switch]$AllowIneligible
    )

    $result = Invoke-AzCliWithRetry `
        -Arguments @(
            "account",
            "show",
            "--subscription",
            $SubscriptionId,
            "--output",
            "json"
        ) `
        -Operation "validar acesso à assinatura" `
        -SubscriptionId $SubscriptionId
    $subscription = ($result.Output -join [Environment]::NewLine) | ConvertFrom-Json

    if ([string]$subscription.tenantId -ne $CurrentTenantId) {
        throw "A assinatura '$SubscriptionId' pertence ao tenant '$($subscription.tenantId)', diferente do tenant atual '$CurrentTenantId'."
    }

    if ([string]$subscription.state -ne "Enabled" -and -not $AllowIneligible) {
        throw "A assinatura '$SubscriptionId' não está habilitada. Estado atual: '$($subscription.state)'."
    }

    return $subscription
}

function Test-CostExportSubscriptionAccess {
    param(
        [Parameter(Mandatory)][string]$SubscriptionId,
        [Parameter(Mandatory)][string]$CurrentTenantId,
        [Parameter(Mandatory)][bool]$DiscoveredByAllSubscriptions
    )

    try {
        $subscription = Get-AccessibleSubscription `
            -SubscriptionId $SubscriptionId `
            -CurrentTenantId $CurrentTenantId `
            -AllowIneligible:$DiscoveredByAllSubscriptions
        if ([string]$subscription.state -ne "Enabled") {
            $summary.IneligibleSubscriptionsSkipped++
            Write-Warning "Assinatura '$SubscriptionId' ignorada por -AllSubscriptions. Estado atual: '$($subscription.state)'."
            return $false
        }

        $exportsListUri = "https://management.azure.com/subscriptions/$SubscriptionId/providers/Microsoft.CostManagement/exports?api-version=$ApiVersion"
        Invoke-AzRestJson `
            -Method get `
            -Uri $exportsListUri `
            -Operation "validar acesso aos Cost Management Exports" `
            -SubscriptionId $SubscriptionId | Out-Null
        $summary.EnabledSubscriptionsProcessed++
        return $true
    }
    catch {
        if ($_.Exception.Message -notmatch "não está habilitada\. Estado atual:") {
            $summary.InaccessibleSubscriptions++
        }
        $summary.Failures.Add("Assinatura '$SubscriptionId' reprovada na validação inicial: $($_.Exception.Message)")
        return $false
    }
}

function Register-AzResourceProviderIfNeeded {
    param(
        [Parameter(Mandatory)][string]$SubscriptionId,
        [Parameter(Mandatory)][string]$Namespace
    )

    $result = Invoke-AzCliWithRetry `
        -Arguments @(
            "provider",
            "show",
            "--namespace",
            $Namespace,
            "--subscription",
            $SubscriptionId,
            "--query",
            "registrationState",
            "--output",
            "tsv"
        ) `
        -Operation "consultar registro do provider $Namespace" `
        -SubscriptionId $SubscriptionId
    $registrationState = ($result.Output -join "").Trim()

    if ($registrationState -eq "Registered") {
        return $true
    }

    $providerScope = "/subscriptions/$SubscriptionId/providers/$Namespace"
    if (-not $PSCmdlet.ShouldProcess($providerScope, "Registrar resource provider")) {
        return $false
    }

    Write-Host "Registrando provider $Namespace na assinatura de destino do Storage..."
    Invoke-AzCliWithRetry `
        -Arguments @(
            "provider",
            "register",
            "--namespace",
            $Namespace,
            "--subscription",
            $SubscriptionId,
            "--wait",
            "--output",
            "none"
        ) `
        -Operation "registrar provider $Namespace" `
        -SubscriptionId $SubscriptionId | Out-Null
    return $true
}

function Test-RoleAssignmentReadAccess {
    param(
        [Parameter(Mandatory)][string]$Scope,
        [Parameter(Mandatory)][string]$SubscriptionId
    )

    try {
        Invoke-AzCliWithRetry `
            -Arguments @(
                "role",
                "assignment",
                "list",
                "--scope",
                $Scope,
                "--query",
                "[0].id",
                "--output",
                "tsv"
            ) `
            -Operation "validar leitura de RBAC no Storage" `
            -SubscriptionId $SubscriptionId | Out-Null
    }
    catch {
        throw "Não foi possível listar atribuições de função no Storage '$Scope'. Confirme acesso de leitura ao Storage e a Microsoft.Authorization/roleAssignments/read. $($_.Exception.Message)"
    }
}

function Get-RoleAssignmentId {
    param(
        [Parameter(Mandatory)][string]$PrincipalId,
        [Parameter(Mandatory)][string]$Scope,
        [Parameter(Mandatory)][string]$SubscriptionId
    )

    $query = "[?scope=='$Scope' && roleDefinitionName=='$storageBlobDataContributorRoleName'].id | [0]"
    $result = Invoke-AzCliWithRetry `
        -Arguments @(
            "role",
            "assignment",
            "list",
            "--assignee",
            $PrincipalId,
            "--scope",
            $Scope,
            "--role",
            $storageBlobDataContributorRoleName,
            "--query",
            $query,
            "--output",
            "tsv"
        ) `
        -Operation "consultar RBAC da Managed Identity" `
        -SubscriptionId $SubscriptionId `
        -RetryOnPrincipalReplication
    return ($result.Output -join "").Trim()
}

function Wait-RoleAssignmentPropagation {
    param(
        [Parameter(Mandatory)][string]$PrincipalId,
        [Parameter(Mandatory)][string]$Scope,
        [Parameter(Mandatory)][string]$SubscriptionId
    )

    for ($attempt = 1; $attempt -le $MaxRetryAttempts; $attempt++) {
        $assignmentId = Get-RoleAssignmentId `
            -PrincipalId $PrincipalId `
            -Scope $Scope `
            -SubscriptionId $SubscriptionId
        if (-not [string]::IsNullOrWhiteSpace($assignmentId)) {
            return
        }

        if ($attempt -eq $MaxRetryAttempts) {
            throw "A atribuição '$storageBlobDataContributorRoleName' não ficou visível no scope '$Scope' após $attempt tentativa(s)."
        }

        $delaySeconds = [Math]::Min(30, [Math]::Pow(2, $attempt))
        Write-Host (
            "Aguardando propagação do RBAC. Tentativa {0}/{1}; nova verificação em {2:N0}s para a assinatura '{3}'." -f
            $attempt,
            $MaxRetryAttempts,
            $delaySeconds,
            $SubscriptionId)
        Start-Sleep -Seconds $delaySeconds
    }
}

function Ensure-ExportStorageRoleAssignment {
    param(
        [Parameter(Mandatory)][string]$PrincipalId,
        [Parameter(Mandatory)][string]$Scope,
        [Parameter(Mandatory)][string]$SubscriptionId,
        [Parameter(Mandatory)][string]$RoleDefinitionId
    )

    try {
        $assignmentId = Get-RoleAssignmentId `
            -PrincipalId $PrincipalId `
            -Scope $Scope `
            -SubscriptionId $SubscriptionId
    }
    catch {
        throw "Falha ao consultar o RBAC da Managed Identity '$PrincipalId'. $($_.Exception.Message)"
    }

    if (-not [string]::IsNullOrWhiteSpace($assignmentId)) {
        Write-Host "RBAC já existente para a Managed Identity '$PrincipalId'; nenhuma duplicação será criada."
        return $true
    }

    if (-not $PSCmdlet.ShouldProcess(
            $Scope,
            "Conceder $storageBlobDataContributorRoleName à Managed Identity $PrincipalId")) {
        return $false
    }

    try {
        Invoke-AzCliWithRetry `
            -Arguments @(
                "role",
                "assignment",
                "create",
                "--assignee-object-id",
                $PrincipalId,
                "--assignee-principal-type",
                "ServicePrincipal",
                "--role",
                $RoleDefinitionId,
                "--scope",
                $Scope,
                "--output",
                "none"
            ) `
            -Operation "criar RBAC da Managed Identity" `
            -SubscriptionId $SubscriptionId `
            -RetryOnPrincipalReplication `
            -AcceptRoleAssignmentExists | Out-Null
    }
    catch {
        $statusCode = $_.Exception.Data["StatusCode"]
        $errorCode = [string]$_.Exception.Data["ErrorCode"]
        $errorMessage = [string]$_.Exception.Data["ErrorMessage"]
        if (($null -ne $statusCode -and [int]$statusCode -eq 403) -or
            (@($errorCode, $errorMessage) -join " ") -match '(?i)AuthorizationFailed|does not have authorization') {
            throw "Sem permissão para criar role assignments no container '$Scope'. O executor precisa de Microsoft.Authorization/roleAssignments/write nesse scope, normalmente via Owner ou User Access Administrator. $($_.Exception.Message)"
        }

        throw
    }

    Write-Host "Aguardando ${rbacInitialPropagationDelaySeconds}s para a propagação inicial do RBAC..."
    Start-Sleep -Seconds $rbacInitialPropagationDelaySeconds
    Wait-RoleAssignmentPropagation `
        -PrincipalId $PrincipalId `
        -Scope $Scope `
        -SubscriptionId $SubscriptionId
    return $true
}

function Format-UtcTimestamp {
    param([Parameter(Mandatory)][DateTimeOffset]$Value)

    return $Value.ToUniversalTime().ToString(
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        [System.Globalization.CultureInfo]::InvariantCulture)
}

function Assert-ExportScheduleCombination {
    param(
        [Parameter(Mandatory)][string]$Timeframe,

        [Parameter(Mandatory)]
        [ValidateSet("Daily", "Monthly")]
        [string]$Recurrence
    )

    if ($Timeframe -eq "BillingMonthToDate" -and $Recurrence -eq "Daily") {
        throw "A combinação BillingMonthToDate + Daily não é suportada pela API de exports. O export diário da plataforma deve usar MonthToDate + Daily."
    }
}

function New-ExportBody {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Usage", "ActualCost", "AmortizedCost")]
        [string]$ExportType,

        [Parameter(Mandatory)][string]$Timeframe,

        [Parameter(Mandatory)]
        [ValidateSet("Daily", "Monthly")]
        [string]$Recurrence,

        [Parameter(Mandatory)][string]$RootFolderPath,
        [Parameter(Mandatory)][string]$StorageResourceId,
        [Parameter(Mandatory)][string]$ManagedIdentityLocation,
        [Parameter(Mandatory)][string]$ScheduleTo
    )

    Assert-ExportScheduleCombination `
        -Timeframe $Timeframe `
        -Recurrence $Recurrence

    $dataSet = @{
        granularity = "Daily"
    }
    if ($ExportType -in @("ActualCost", "AmortizedCost")) {
        $dataSet["configuration"] = @{
            dataVersion = "2023-05-01"
        }
    }

    return @{
        identity = @{
            type = "SystemAssigned"
        }
        location = $ManagedIdentityLocation
        properties = @{
            format = "Csv"
            compressionMode = $CompressionMode
            dataOverwriteBehavior = "OverwritePreviousReport"
            partitionData = $PartitionData
            exportDescription = "Export de custos consumido pela Azure FinOps Platform."
            definition = @{
                type = $ExportType
                timeframe = $Timeframe
                dataSet = $dataSet
            }
            deliveryInfo = @{
                destination = @{
                    type = "AzureBlob"
                    resourceId = $StorageResourceId
                    container = $ContainerName
                    rootFolderPath = $RootFolderPath
                }
            }
            schedule = @{
                status = "Inactive"
                recurrence = $Recurrence
                recurrencePeriod = @{
                    from = Format-UtcTimestamp -Value ([DateTimeOffset]::UtcNow.AddMinutes(10))
                    to = $ScheduleTo
                }
            }
        }
    }
}

function Write-ExecutionSummary {
    Write-Host ""
    Write-Host "Resumo dos Cost Management Exports:"
    Write-Host "  Criados: $($summary.Created)"
    Write-Host "  Atualizados: $($summary.Updated)"
    Write-Host "  Ignorados: $($summary.Skipped)"
    Write-Host "  Ativados: $($summary.Activated)"
    Write-Host "  Execução solicitada: $($summary.RunRequested)"
    Write-Host "  Assinaturas habilitadas processadas: $($summary.EnabledSubscriptionsProcessed)"
    Write-Host "  Assinaturas desativadas/inelegíveis ignoradas: $($summary.IneligibleSubscriptionsSkipped)"
    Write-Host "  Assinaturas inacessíveis ou de outro tenant: $($summary.InaccessibleSubscriptions)"
    Write-Host "  Falhas: $($summary.Failures.Count)"
    Write-Host "  Tipo utilizado por assinatura:"
    foreach ($subscriptionId in $summary.ExportTypes.Keys) {
        Write-Host "    $subscriptionId -> $($summary.ExportTypes[$subscriptionId])"
    }

    foreach ($failure in $summary.Failures) {
        Write-Warning "  - $failure"
    }
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI (az) não está instalado ou não está disponível no PATH."
}

if ($RunNow -and -not $Activate) {
    throw "-RunNow só pode ser usado junto com -Activate."
}

if ($IncludeLastBillingPeriod -and $ExportName -eq $lastBillingExportName) {
    throw "-ExportName deve ser diferente de '$lastBillingExportName' quando -IncludeLastBillingPeriod for usado."
}

$exportDefinitions = [System.Collections.Generic.List[object]]::new()
$exportDefinitions.Add([pscustomobject]@{
        Name = $ExportName
        Timeframe = "MonthToDate"
        Recurrence = "Daily"
        BillingPeriod = "current"
    })
if ($IncludeLastBillingPeriod) {
    $exportDefinitions.Add([pscustomobject]@{
            Name = $lastBillingExportName
            Timeframe = "TheLastBillingMonth"
            Recurrence = "Monthly"
            BillingPeriod = "last"
        })
}
foreach ($exportDefinition in $exportDefinitions) {
    Assert-ExportScheduleCombination `
        -Timeframe $exportDefinition.Timeframe `
        -Recurrence $exportDefinition.Recurrence
}

if ($AllSubscriptions -and $SubscriptionIds -and $SubscriptionIds.Count -gt 0) {
    throw "Use -SubscriptionIds ou -AllSubscriptions, não ambos."
}

if ($AllSubscriptions -and [string]::IsNullOrWhiteSpace($ManagementGroupId)) {
    throw "-ManagementGroupId é obrigatório quando -AllSubscriptions é usado."
}

if (-not $AllSubscriptions -and (-not $SubscriptionIds -or $SubscriptionIds.Count -eq 0)) {
    throw "Informe explicitamente -SubscriptionIds ou use -AllSubscriptions."
}

$minimumScheduleEnd = [DateTimeOffset]::UtcNow.AddMinutes(10)
if ($ScheduleEndUtc.ToUniversalTime() -le $minimumScheduleEnd) {
    throw "-ScheduleEndUtc deve ser posterior a '$((Format-UtcTimestamp -Value $minimumScheduleEnd))'."
}
$scheduleTo = Format-UtcTimestamp -Value $ScheduleEndUtc

try {
    $accountResult = Invoke-AzCliWithRetry `
        -Arguments @("account", "show", "--output", "json") `
        -Operation "validar sessão do Azure CLI"
    $currentAccount = ($accountResult.Output -join [Environment]::NewLine) | ConvertFrom-Json
}
catch {
    throw "Execute 'az login' e selecione o tenant correto antes de rodar este script. $($_.Exception.Message)"
}
$currentTenantId = [string]$currentAccount.tenantId

$resolvedSubscriptionIds = if ($SubscriptionIds -and $SubscriptionIds.Count -gt 0) {
    $SubscriptionIds
} else {
    Get-ManagementGroupSubscriptionIds -GroupId $ManagementGroupId
}

$resolvedSubscriptionIds = @(
    $resolvedSubscriptionIds |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Trim() } |
        Sort-Object -Unique
)

if ($resolvedSubscriptionIds.Count -eq 0) {
    throw "Nenhuma assinatura foi encontrada no Management Group '$ManagementGroupId'."
}

Get-AccessibleSubscription `
    -SubscriptionId $StorageSubscriptionId `
    -CurrentTenantId $currentTenantId | Out-Null

$storageResourceId = "/subscriptions/$StorageSubscriptionId/resourceGroups/$StorageResourceGroup/providers/Microsoft.Storage/storageAccounts/$StorageAccountName"
$storageUri = "https://management.azure.com$storageResourceId`?api-version=$storageApiVersion"
$storageAccount = Get-AzResourceIfExists `
    -Uri $storageUri `
    -Operation "validar Storage Account central" `
    -SubscriptionId $StorageSubscriptionId
if ($null -eq $storageAccount) {
    throw "O Storage Account '$StorageAccountName' não existe no resource group '$StorageResourceGroup' da assinatura '$StorageSubscriptionId'."
}

$managedIdentityLocation = if ([string]::IsNullOrWhiteSpace($Location)) {
    [string]$storageAccount.location
} else {
    $Location.Trim()
}
if ([string]::IsNullOrWhiteSpace($managedIdentityLocation)) {
    throw "Não foi possível determinar a localização da Managed Identity do export. Informe -Location explicitamente."
}
Write-Host "Localização da Managed Identity dos exports: $managedIdentityLocation."

Test-RoleAssignmentReadAccess `
    -Scope $storageResourceId `
    -SubscriptionId $StorageSubscriptionId

$validatedSubscriptionIds = [System.Collections.Generic.List[string]]::new()
foreach ($subscriptionId in $resolvedSubscriptionIds) {
    if (Test-CostExportSubscriptionAccess `
            -SubscriptionId $subscriptionId `
            -CurrentTenantId $currentTenantId `
            -DiscoveredByAllSubscriptions ([bool]$AllSubscriptions)) {
        $validatedSubscriptionIds.Add($subscriptionId)
    }
}

if ($validatedSubscriptionIds.Count -eq 0) {
    Write-ExecutionSummary
    if ($summary.Failures.Count -gt 0) {
        throw "Nenhuma assinatura válida e acessível permaneceu após a validação inicial."
    }

    Write-Host "Nenhuma assinatura habilitada foi encontrada; assinaturas inelegíveis foram ignoradas intencionalmente."
    return
}

$providerReady = Register-AzResourceProviderIfNeeded `
    -SubscriptionId $StorageSubscriptionId `
    -Namespace "Microsoft.CostManagementExports"
if (-not $providerReady -and -not $WhatIfPreference) {
    throw "O provider Microsoft.CostManagementExports precisa estar registrado na assinatura do Storage antes da criação dos exports."
}

$containerResourceId = "$storageResourceId/blobServices/default/containers/$ContainerName"
$containerUri = "https://management.azure.com$containerResourceId`?api-version=$storageApiVersion"
$existingContainer = Get-AzResourceIfExists `
    -Uri $containerUri `
    -Operation "verificar container de exports" `
    -SubscriptionId $StorageSubscriptionId
if ($null -eq $existingContainer) {
    if ($PSCmdlet.ShouldProcess($containerResourceId, "Criar container privado de exports")) {
        Invoke-AzRestJson `
            -Method put `
            -Uri $containerUri `
            -Body @{
                properties = @{
                    publicAccess = "None"
                }
            } `
            -Operation "criar container de exports" `
            -SubscriptionId $StorageSubscriptionId | Out-Null
    }
    elseif (-not $WhatIfPreference) {
        throw "O container '$ContainerName' não existe e sua criação não foi confirmada."
    }
}

$roleDefinitionId = "/subscriptions/$StorageSubscriptionId/providers/Microsoft.Authorization/roleDefinitions/$storageBlobDataContributorRoleId"

if ($PartitionData) {
    Write-Warning "PartitionData está habilitado. O consumidor deve processar o manifest.json e todos os arquivos nele listados."
}

foreach ($subscriptionId in $validatedSubscriptionIds) {
    $selectedExportType = $ExportType
    $summary.ExportTypes[$subscriptionId] = $selectedExportType

    foreach ($exportDefinition in $exportDefinitions) {
        $exportResourceId = "/subscriptions/$subscriptionId/providers/Microsoft.CostManagement/exports/$($exportDefinition.Name)"
        $exportUri = "https://management.azure.com$exportResourceId`?api-version=$ApiVersion"
        $rootFolderPath = "subscriptions/$subscriptionId/billing-period/$($exportDefinition.BillingPeriod)"

        try {
            $existingExport = Get-AzResourceIfExists `
                -Uri $exportUri `
                -Operation "verificar export '$($exportDefinition.Name)'" `
                -SubscriptionId $subscriptionId
            if ($null -ne $existingExport -and -not $Force) {
                Write-Warning "O export '$exportResourceId' já existe e foi ignorado. Use -Force para atualizá-lo."
                $summary.Skipped++
                continue
            }

            $body = New-ExportBody `
                -ExportType $selectedExportType `
                -Timeframe $exportDefinition.Timeframe `
                -Recurrence $exportDefinition.Recurrence `
                -RootFolderPath $rootFolderPath `
                -StorageResourceId $storageResourceId `
                -ManagedIdentityLocation $managedIdentityLocation `
                -ScheduleTo $scheduleTo

            $operation = if ($null -eq $existingExport) {
                "Criar Cost Management Export inativo"
            } else {
                "Atualizar Cost Management Export como inativo"
            }
            if ($null -ne $existingExport) {
                if ([string]::IsNullOrWhiteSpace([string]$existingExport.eTag)) {
                    throw "O export existente '$exportResourceId' não retornou eTag e não pode ser atualizado com segurança."
                }
                $body.eTag = [string]$existingExport.eTag
            }

            if (-not $PSCmdlet.ShouldProcess($exportResourceId, $operation)) {
                $summary.Skipped++
                continue
            }

            Write-Host "Configurando export '$($exportDefinition.Name)' para a assinatura '$subscriptionId'..."
            $configuredExport = Invoke-AzRestJson `
                -Method put `
                -Uri $exportUri `
                -Body $body `
                -Operation $operation `
                -SubscriptionId $subscriptionId `
                -RetryOnFailedIdentityOperation
            $summary.ExportTypes[$subscriptionId] = $selectedExportType

            if ($null -eq $existingExport) {
                $summary.Created++
            } else {
                $summary.Updated++
            }

            $principalId = [string]$configuredExport.identity.principalId
            if ([string]::IsNullOrWhiteSpace($principalId)) {
                throw "O Azure não retornou a Managed Identity do export '$exportResourceId'."
            }

            $roleReady = Ensure-ExportStorageRoleAssignment `
                -PrincipalId $principalId `
                -Scope $containerResourceId `
                -SubscriptionId $subscriptionId `
                -RoleDefinitionId $roleDefinitionId
            if (-not $roleReady) {
                throw "A atribuição de RBAC não foi confirmada; o export permanecerá inativo."
            }

            if (-not $Activate) {
                Write-Host "Export '$($exportDefinition.Name)' criado ou atualizado como inativo. Reexecute com -Activate -Force após validar."
                continue
            }

            if ([string]::IsNullOrWhiteSpace([string]$configuredExport.eTag)) {
                throw "O Azure não retornou o eTag atualizado de '$exportResourceId'; a ativação segura não será tentada."
            }
            $body.eTag = [string]$configuredExport.eTag
            $body.properties.schedule.status = "Active"
            $body.properties.schedule.recurrencePeriod.from = Format-UtcTimestamp `
                -Value ([DateTimeOffset]::UtcNow.AddMinutes(10))
            if (-not $PSCmdlet.ShouldProcess($exportResourceId, "Ativar Cost Management Export")) {
                Write-Warning "A ativação de '$exportResourceId' não foi confirmada; o export permanece inativo."
                continue
            }

            $activatedExport = Invoke-AzRestJson `
                -Method put `
                -Uri $exportUri `
                -Body $body `
                -Operation "ativar Cost Management Export" `
                -SubscriptionId $subscriptionId `
                -RetryOnFailedIdentityOperation
            $summary.Activated++
            Write-Host "Export '$($exportDefinition.Name)' ativado para '$subscriptionId' em '$ContainerName/$rootFolderPath'."

            if ($RunNow) {
                $runUri = "https://management.azure.com$exportResourceId/run?api-version=$ApiVersion"
                if ($PSCmdlet.ShouldProcess($exportResourceId, "Solicitar execução imediata do export")) {
                    Invoke-AzRestJson `
                        -Method post `
                        -Uri $runUri `
                        -Operation "solicitar execução imediata do export" `
                        -SubscriptionId $subscriptionId | Out-Null
                    $summary.RunRequested++
                    Write-Host "Execução solicitada para '$($exportDefinition.Name)'. O arquivo será gerado de forma assíncrona."
                }
            }

            $null = $activatedExport
        }
        catch {
            $failure = "Assinatura '$subscriptionId', export '$($exportDefinition.Name)': $($_.Exception.Message)"
            $summary.Failures.Add($failure)
            Write-Error $failure -ErrorAction Continue
        }
    }
}

Write-ExecutionSummary
if ($summary.Failures.Count -gt 0) {
    throw "O processamento terminou com $($summary.Failures.Count) falha(s). As demais assinaturas e exports foram processados."
}
