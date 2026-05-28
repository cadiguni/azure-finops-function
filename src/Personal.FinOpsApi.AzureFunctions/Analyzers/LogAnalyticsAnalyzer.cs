using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;

namespace Personal.FinOpsApi.AzureFunctions.Analyzers;

/// <summary>
/// Analyzer para detectar oportunidades de otimização em workspaces do Azure Log Analytics.
/// Analisa retenção, ingestão de dados e identifica tabelas com configurações não-otimizadas.
/// 
/// Regras de análise:
/// - Workspaces com retenção > 30 dias (configurável)
/// - Workspaces com retenção > 90 dias (alta prioridade)
/// - Tabelas com retenção customizada > 30 dias
/// - Workspaces sem ingestão relevante (baixo uso)
/// - Workspaces com alta ingestão diária
/// </summary>
public class LogAnalyticsAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;
    private readonly HttpRetryService _httpRetryService;
    private readonly ResourceCostLookupService _costLookupService;
    private readonly ILogger<LogAnalyticsAnalyzer> _logger;

    // Configurações (podem ser sobrescritas via Environment Variables)
    private readonly int _desiredRetentionDays;
    private readonly int _highRetentionDays;
    private readonly decimal _lowIngestionThresholdGbPerDay;
    private readonly decimal _highIngestionThresholdGbPerDay;
    private readonly bool _isEnabled;

    public LogAnalyticsAnalyzer(
        HttpClient httpClient,
        HttpRetryService httpRetryService,
        ResourceCostLookupService costLookupService,
        ILogger<LogAnalyticsAnalyzer> logger)
    {
        _httpClient = httpClient;
        _credential = new DefaultAzureCredential();
        _httpRetryService = httpRetryService;
        _costLookupService = costLookupService;
        _logger = logger;

        // Carregar configurações do ambiente
        _desiredRetentionDays = int.TryParse(Environment.GetEnvironmentVariable("LogAnalyticsDesiredRetentionDays"), out var desired) ? desired : 30;
        _highRetentionDays = int.TryParse(Environment.GetEnvironmentVariable("LogAnalyticsHighRetentionDays"), out var high) ? high : 90;
        _lowIngestionThresholdGbPerDay = decimal.TryParse(Environment.GetEnvironmentVariable("LogAnalyticsLowIngestionThresholdGbPerDay"), out var lowIng) ? lowIng : 0.01m;
        _highIngestionThresholdGbPerDay = decimal.TryParse(Environment.GetEnvironmentVariable("LogAnalyticsHighIngestionThresholdGbPerDay"), out var highIng) ? highIng : 5m;
        _isEnabled = !Environment.GetEnvironmentVariable("EnableLogAnalyticsAnalysis")?.Equals("false", StringComparison.OrdinalIgnoreCase) ?? true;
    }

    /// <summary>
    /// Analisa workspaces do Log Analytics na subscription
    /// Identifica problemas de retenção e ingestão
    /// </summary>
    public async Task<StandardAnalyzerResult> AnalyzeAsync(string subscriptionId, int analysisPeriodDays = 30, bool dryRun = true)
    {
        var findings = new List<StandardFinding>();
        var workspacesAnalyzed = 0;
        var tablesAnalyzed = 0;

        if (!_isEnabled)
        {
            _logger.LogInformation("⏭️ [LOG-ANALYTICS-ANALYZER] Análise desabilitada via EnableLogAnalyticsAnalysis=false");
            return CreateEmptyResult(subscriptionId, analysisPeriodDays, dryRun, "disabled");
        }

        try
        {
            _logger.LogInformation("🔍 [LOG-ANALYTICS-ANALYZER] Iniciando análise de workspaces para {subscriptionId}", subscriptionId);
            _logger.LogInformation("📊 Configurações: DesiredRetention={desired}d, HighRetention={high}d, LowIngestion={low}GB/d, HighIngestion={highIng}GB/d",
                _desiredRetentionDays, _highRetentionDays, _lowIngestionThresholdGbPerDay, _highIngestionThresholdGbPerDay);

            // Pre-carregar custos do Cost Management para esta subscription
            await _costLookupService.PreloadCostsAsync(subscriptionId);

            // 1. Listar workspaces via Resource Graph
            var workspaces = await ListWorkspacesAsync(subscriptionId);
            _logger.LogInformation("📦 [LOG-ANALYTICS-ANALYZER] Encontrados {count} workspaces", workspaces.Count);

            foreach (var workspace in workspaces)
            {
                try
                {
                    var resourceId = workspace.GetProperty("resourceId").GetString() ?? "";
                    var name = workspace.GetProperty("name").GetString() ?? "";
                    var location = workspace.GetProperty("location").GetString() ?? "";
                    var resourceGroup = workspace.GetProperty("resourceGroup").GetString() ?? "";
                    var workspaceId = workspace.TryGetProperty("workspaceId", out var wsId) ? wsId.GetString() ?? "" : "";

                    // Extrair retenção do workspace (em dias)
                    var retentionDays = 30; // Default
                    if (workspace.TryGetProperty("retentionInDays", out var retentionEl) && retentionEl.ValueKind == JsonValueKind.Number)
                    {
                        retentionDays = retentionEl.GetInt32();
                    }

                    // Extrair SKU para identificar workspaces LABasedCapacityReservation ou Sentinel
                    var sku = workspace.TryGetProperty("sku", out var skuEl) ? skuEl.GetString() ?? "PerGB2018" : "PerGB2018";
                    var isSentinelEnabled = await CheckSentinelEnabledAsync(resourceId);

                    _logger.LogDebug("🔍 Analisando workspace: {name} (Retention={retention}d, SKU={sku}, Sentinel={sentinel})", 
                        name, retentionDays, sku, isSentinelEnabled);

                    workspacesAnalyzed++;

                    // 2. Buscar custo real do Cost Management
                    var costData = await _costLookupService.GetResourceCostDataAsync(subscriptionId, resourceId);
                    var dailyCost = costData.DailyCost;
                    var estimatedMonthlyCost = costData.MonthlyCost;

                    // 3. Buscar dados de ingestão (últimos 30 dias)
                    var ingestionData = await GetWorkspaceIngestionAsync(resourceId, analysisPeriodDays);
                    var dailyIngestionGb = ingestionData.DailyAverageGb;
                    var totalIngestionGb = ingestionData.TotalGb;
                    var topTables = ingestionData.TopTables;

                    // 4. Buscar retenção por tabela
                    var tableRetentions = await GetTableRetentionsAsync(resourceId);
                    tablesAnalyzed += tableRetentions.Count;

                    // ====== REGRAS DE ANÁLISE ======

                    // Regra 1: Workspace com retenção muito elevada (> 90 dias)
                    if (retentionDays > _highRetentionDays)
                    {
                        var priority = FindingPriorities.HIGH;
                        var description = isSentinelEnabled
                            ? $"Workspace '{name}' com retenção de {retentionDays} dias (Sentinel detectado). Validar necessidade de compliance/auditoria."
                            : $"Workspace '{name}' com retenção muito elevada ({retentionDays} dias). Padrão desejado: {_desiredRetentionDays} dias.";

                        findings.Add(CreateFinding(
                            type: FindingTypes.LOG_ANALYTICS_HIGH_RETENTION,
                            resourceId: resourceId,
                            name: name,
                            location: location,
                            resourceGroup: resourceGroup,
                            subscriptionId: subscriptionId,
                            priority: priority,
                            confidence: isSentinelEnabled ? 0.6 : 0.85,
                            description: description,
                            recommendation: "Investigar necessidade de retenção elevada. Verificar requisitos de compliance, auditoria ou Sentinel.",
                            dailyCost: dailyCost,
                            monthlyCost: estimatedMonthlyCost,
                            monthlySavings: 0, // Não podemos estimar economia de retenção diretamente
                            metadata: new Dictionary<string, object>
                            {
                                { "retentionInDays", retentionDays },
                                { "desiredRetentionInDays", _desiredRetentionDays },
                                { "dailyIngestionGb", dailyIngestionGb },
                                { "totalIngestionGb", totalIngestionGb },
                                { "sku", sku },
                                { "isSentinelEnabled", isSentinelEnabled },
                                { "topTables", topTables.Take(5).ToList() }
                            },
                            tags: ExtractTags(workspace)
                        ));
                    }
                    // Regra 2: Workspace com retenção > desejado (> 30 dias)
                    else if (retentionDays > _desiredRetentionDays)
                    {
                        var priority = FindingPriorities.MEDIUM;
                        var description = isSentinelEnabled
                            ? $"Workspace '{name}' com retenção de {retentionDays} dias (Sentinel detectado). Padrão desejado: {_desiredRetentionDays} dias."
                            : $"Workspace '{name}' com retenção de {retentionDays} dias, acima do padrão desejado de {_desiredRetentionDays} dias.";

                        findings.Add(CreateFinding(
                            type: FindingTypes.LOG_ANALYTICS_RETENTION,
                            resourceId: resourceId,
                            name: name,
                            location: location,
                            resourceGroup: resourceGroup,
                            subscriptionId: subscriptionId,
                            priority: priority,
                            confidence: isSentinelEnabled ? 0.5 : 0.75,
                            description: description,
                            recommendation: "Investigar se retenção pode ser reduzida para o padrão de 30 dias. Verificar requisitos de compliance.",
                            dailyCost: dailyCost,
                            monthlyCost: estimatedMonthlyCost,
                            monthlySavings: 0,
                            metadata: new Dictionary<string, object>
                            {
                                { "retentionInDays", retentionDays },
                                { "desiredRetentionInDays", _desiredRetentionDays },
                                { "dailyIngestionGb", dailyIngestionGb },
                                { "sku", sku },
                                { "isSentinelEnabled", isSentinelEnabled }
                            },
                            tags: ExtractTags(workspace)
                        ));
                    }

                    // Regra 3: Workspace sem ingestão relevante (baixo uso)
                    if (dailyIngestionGb < _lowIngestionThresholdGbPerDay && dailyIngestionGb >= 0)
                    {
                        findings.Add(CreateFinding(
                            type: FindingTypes.LOG_ANALYTICS_LOW_INGESTION,
                            resourceId: resourceId,
                            name: name,
                            location: location,
                            resourceGroup: resourceGroup,
                            subscriptionId: subscriptionId,
                            priority: FindingPriorities.MEDIUM,
                            confidence: 0.8,
                            description: $"Workspace '{name}' com baixa ou nenhuma ingestão nos últimos {analysisPeriodDays} dias ({dailyIngestionGb:F3} GB/dia). Avaliar se ainda é necessário.",
                            recommendation: "Investigar se o workspace ainda é utilizado. Verificar se fontes de dados estão configuradas corretamente.",
                            dailyCost: dailyCost,
                            monthlyCost: estimatedMonthlyCost,
                            monthlySavings: estimatedMonthlyCost * 0.9m, // 90% economia se remover workspace não utilizado
                            metadata: new Dictionary<string, object>
                            {
                                { "dailyIngestionGb", dailyIngestionGb },
                                { "totalIngestionGb", totalIngestionGb },
                                { "lowIngestionThresholdGbPerDay", _lowIngestionThresholdGbPerDay },
                                { "analysisPeriodDays", analysisPeriodDays },
                                { "sku", sku }
                            },
                            tags: ExtractTags(workspace)
                        ));
                    }

                    // Regra 4: Workspace com alta ingestão diária
                    if (dailyIngestionGb > _highIngestionThresholdGbPerDay)
                    {
                        var topTablesList = topTables.Take(5).ToList();
                        findings.Add(CreateFinding(
                            type: FindingTypes.LOG_ANALYTICS_HIGH_INGESTION,
                            resourceId: resourceId,
                            name: name,
                            location: location,
                            resourceGroup: resourceGroup,
                            subscriptionId: subscriptionId,
                            priority: FindingPriorities.HIGH,
                            confidence: 0.85,
                            description: $"Workspace '{name}' com alto volume de ingestão ({dailyIngestionGb:F2} GB/dia). Revisar Diagnostic Settings e fontes de coleta.",
                            recommendation: "Investigar fontes de logs com maior volume. Revisar Diagnostic Settings, Application Insights e nível de verbosidade dos logs.",
                            dailyCost: dailyCost,
                            monthlyCost: estimatedMonthlyCost,
                            monthlySavings: 0, // Requer análise específica para estimar economia
                            metadata: new Dictionary<string, object>
                            {
                                { "dailyIngestionGb", dailyIngestionGb },
                                { "totalIngestionGb", totalIngestionGb },
                                { "highIngestionThresholdGbPerDay", _highIngestionThresholdGbPerDay },
                                { "topTables", topTablesList },
                                { "sku", sku }
                            },
                            tags: ExtractTags(workspace)
                        ));
                    }

                    // Regra 5: Tabelas com retenção customizada > desejado
                    var tablesWithHighRetention = tableRetentions.Where(t => t.RetentionDays > _desiredRetentionDays).ToList();
                    if (tablesWithHighRetention.Any())
                    {
                        var affectedTableNames = tablesWithHighRetention.Select(t => t.TableName).ToList();
                        var maxTableRetention = tablesWithHighRetention.Max(t => t.RetentionDays);

                        findings.Add(CreateFinding(
                            type: FindingTypes.LOG_ANALYTICS_TABLE_RETENTION,
                            resourceId: resourceId,
                            name: name,
                            location: location,
                            resourceGroup: resourceGroup,
                            subscriptionId: subscriptionId,
                            priority: maxTableRetention > _highRetentionDays ? FindingPriorities.HIGH : FindingPriorities.MEDIUM,
                            confidence: 0.7,
                            description: $"Workspace '{name}' possui {tablesWithHighRetention.Count} tabela(s) com retenção acima de {_desiredRetentionDays} dias.",
                            recommendation: "Investigar tabelas com retenção elevada. Verificar se a retenção customizada é necessária para compliance.",
                            dailyCost: dailyCost,
                            monthlyCost: estimatedMonthlyCost,
                            monthlySavings: 0,
                            metadata: new Dictionary<string, object>
                            {
                                { "affectedTables", affectedTableNames },
                                { "maxTableRetentionDays", maxTableRetention },
                                { "desiredRetentionInDays", _desiredRetentionDays },
                                { "tablesWithHighRetention", tablesWithHighRetention.Take(10).Select(t => new { t.TableName, t.RetentionDays }).ToList() }
                            },
                            tags: ExtractTags(workspace)
                        ));
                    }
                }
                catch (Exception workspaceEx)
                {
                    _logger.LogWarning(workspaceEx, "⚠️ Erro ao analisar workspace. Continuando com próximo workspace.");
                }
            }

            _logger.LogInformation("✅ [LOG-ANALYTICS-ANALYZER] Análise concluída: {findings} recomendações, {workspaces} workspaces, {tables} tabelas analisadas",
                findings.Count, workspacesAnalyzed, tablesAnalyzed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [LOG-ANALYTICS-ANALYZER] Erro durante análise de Log Analytics workspaces");
        }

        return new StandardAnalyzerResult
        {
            SchemaVersion = "1.0",
            AnalysisId = Guid.NewGuid().ToString(),
            Analyzer = AnalyzerNames.LOG_ANALYTICS_ANALYZER,
            SubscriptionId = subscriptionId,
            ExecutedAt = DateTime.UtcNow,
            AnalysisPeriodDays = analysisPeriodDays,
            DryRun = dryRun,
            Findings = findings,
            ExecutionMetadata = new Dictionary<string, object>
            {
                { "workspacesAnalyzed", workspacesAnalyzed },
                { "tablesAnalyzed", tablesAnalyzed },
                { "analyzerVersion", "1.0" },
                { "desiredRetentionDays", _desiredRetentionDays },
                { "highRetentionDays", _highRetentionDays },
                { "lowIngestionThresholdGbPerDay", _lowIngestionThresholdGbPerDay },
                { "highIngestionThresholdGbPerDay", _highIngestionThresholdGbPerDay }
            }
        };
    }

    /// <summary>
    /// Lista workspaces do Log Analytics via Resource Graph
    /// </summary>
    private async Task<List<JsonElement>> ListWorkspacesAsync(string subscriptionId)
    {
        var kqlQuery = $@"
            Resources
            | where type =~ 'microsoft.operationalinsights/workspaces'
            | where subscriptionId =~ '{subscriptionId}'
            | project
                resourceId = id,
                name,
                resourceGroup,
                subscriptionId,
                location,
                workspaceId = properties.customerId,
                retentionInDays = properties.retentionInDays,
                sku = properties.sku.name,
                features = properties.features,
                tags
        ";

        var token = await _credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }));

        var resourceGraphPayload = new { query = kqlQuery };
        var jsonPayload = JsonSerializer.Serialize(resourceGraphPayload);
        var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");

        var response = await _httpRetryService.PostWithRetryAsync(
            _httpClient,
            "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01",
            content);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("⚠️ Resource Graph API retornou {status}", response.StatusCode);
            return new List<JsonElement>();
        }

        var jsonResponse = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(jsonResponse);

        return doc.RootElement.GetProperty("data").EnumerateArray().ToList();
    }

    /// <summary>
    /// Verifica se Sentinel está habilitado no workspace
    /// </summary>
    private async Task<bool> CheckSentinelEnabledAsync(string workspaceResourceId)
    {
        try
        {
            // Verificar se existe a solution SecurityInsights (Sentinel)
            var sentinelResourceId = $"{workspaceResourceId}/providers/Microsoft.SecurityInsights/settings/main";

            var token = await _credential.GetTokenAsync(
                new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }));

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");

            // Alternativa: verificar via Resource Graph se há SecurityInsights
            var kqlQuery = $@"
                Resources
                | where type =~ 'microsoft.securityinsights/settings' or type =~ 'microsoft.securityinsights/onboardingstates'
                | where id startswith '{workspaceResourceId}'
                | count
            ";

            var resourceGraphPayload = new { query = kqlQuery };
            var jsonPayload = JsonSerializer.Serialize(resourceGraphPayload);
            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpRetryService.PostWithRetryAsync(
                _httpClient,
                "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01",
                content);

            if (response.IsSuccessStatusCode)
            {
                var jsonResponse = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(jsonResponse);
                var count = doc.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault()
                    .TryGetProperty("count_", out var countEl) ? countEl.GetInt32() : 0;
                return count > 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Não foi possível verificar Sentinel para {workspace}: {error}", workspaceResourceId, ex.Message);
        }

        return false;
    }

    /// <summary>
    /// Obtém dados de ingestão do workspace via Azure Monitor Metrics ou Log Analytics Query
    /// </summary>
    private async Task<WorkspaceIngestionData> GetWorkspaceIngestionAsync(string workspaceResourceId, int analysisPeriodDays)
    {
        var result = new WorkspaceIngestionData();

        try
        {
            // Usar Azure Monitor Metrics para obter ingestão
            var token = await _credential.GetTokenAsync(
                new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }));

            var endTime = DateTime.UtcNow;
            var startTime = endTime.AddDays(-analysisPeriodDays);

            // Métrica: microsoft.operationalinsights/workspaces - BillableDataIngestedInGB
            var metricsUrl = $"https://management.azure.com{workspaceResourceId}/providers/microsoft.insights/metrics?" +
                            $"api-version=2023-10-01&metricnames=BillableDataIngestedInBytes&" +
                            $"timespan={startTime:yyyy-MM-ddTHH:mm:ssZ}/{endTime:yyyy-MM-ddTHH:mm:ssZ}&" +
                            $"interval=P1D&aggregation=total";

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");

            var response = await _httpRetryService.GetWithRetryAsync(_httpClient, metricsUrl);

            if (response.IsSuccessStatusCode)
            {
                var jsonResponse = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(jsonResponse);

                var metrics = doc.RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
                if (metrics.ValueKind != JsonValueKind.Undefined && metrics.TryGetProperty("timeseries", out var timeseries))
                {
                    var dataPoints = timeseries.EnumerateArray().FirstOrDefault()
                        .GetProperty("data").EnumerateArray().ToList();

                    decimal totalBytes = 0;
                    foreach (var point in dataPoints)
                    {
                        if (point.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number)
                        {
                            totalBytes += Convert.ToDecimal(total.GetDouble());
                        }
                    }

                    result.TotalGb = totalBytes / (1024 * 1024 * 1024); // Bytes para GB
                    result.DailyAverageGb = dataPoints.Count > 0 ? result.TotalGb / dataPoints.Count : 0;
                }
            }

            // Tentar obter top tabelas via Log Analytics Query (opcional, pode falhar)
            result.TopTables = await GetTopTablesAsync(workspaceResourceId, analysisPeriodDays);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Não foi possível obter dados de ingestão para {workspace}: {error}", workspaceResourceId, ex.Message);
            result.DailyAverageGb = -1; // Indicador de dados indisponíveis
        }

        return result;
    }

    /// <summary>
    /// Obtém as tabelas com maior ingestão (quando possível)
    /// </summary>
    private async Task<List<string>> GetTopTablesAsync(string workspaceResourceId, int analysisPeriodDays)
    {
        var topTables = new List<string>();

        try
        {
            // Extrair workspace ID do resource ID
            // /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.OperationalInsights/workspaces/{name}
            var parts = workspaceResourceId.Split('/');
            var workspaceName = parts.LastOrDefault() ?? "";

            // Query KQL para top tabelas por ingestão
            // Nota: Isso requer permissões de leitura no workspace
            var kqlQuery = $@"
                Usage
                | where TimeGenerated > ago({analysisPeriodDays}d)
                | summarize TotalGB = sum(Quantity) / 1024 by DataType
                | top 10 by TotalGB desc
                | project DataType
            ";

            var token = await _credential.GetTokenAsync(
                new Azure.Core.TokenRequestContext(new[] { "https://api.loganalytics.io/.default" }));

            var queryPayload = new { query = kqlQuery };
            var jsonPayload = JsonSerializer.Serialize(queryPayload);
            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");

            // Usar a API do Log Analytics workspace
            var queryUrl = $"https://management.azure.com{workspaceResourceId}/api/query?api-version=2022-10-01";

            var response = await _httpRetryService.PostWithRetryAsync(_httpClient, queryUrl, content);

            if (response.IsSuccessStatusCode)
            {
                var jsonResponse = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(jsonResponse);

                if (doc.RootElement.TryGetProperty("tables", out var tables))
                {
                    var rows = tables.EnumerateArray().FirstOrDefault()
                        .GetProperty("rows").EnumerateArray();
                    
                    foreach (var row in rows)
                    {
                        var tableName = row.EnumerateArray().FirstOrDefault().GetString();
                        if (!string.IsNullOrEmpty(tableName))
                        {
                            topTables.Add(tableName);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Não foi possível obter top tabelas: {error}", ex.Message);
        }

        return topTables;
    }

    /// <summary>
    /// Obtém retenção configurada por tabela
    /// </summary>
    private async Task<List<TableRetentionInfo>> GetTableRetentionsAsync(string workspaceResourceId)
    {
        var tableRetentions = new List<TableRetentionInfo>();

        try
        {
            var token = await _credential.GetTokenAsync(
                new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }));

            // Listar tabelas do workspace
            var tablesUrl = $"https://management.azure.com{workspaceResourceId}/tables?api-version=2022-10-01";

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");

            var response = await _httpRetryService.GetWithRetryAsync(_httpClient, tablesUrl);

            if (response.IsSuccessStatusCode)
            {
                var jsonResponse = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(jsonResponse);

                if (doc.RootElement.TryGetProperty("value", out var tablesArray))
                {
                    foreach (var table in tablesArray.EnumerateArray())
                    {
                        var tableName = table.GetProperty("name").GetString() ?? "";
                        var retentionDays = 30; // Default

                        if (table.TryGetProperty("properties", out var props))
                        {
                            if (props.TryGetProperty("retentionInDays", out var retention) && retention.ValueKind == JsonValueKind.Number)
                            {
                                retentionDays = retention.GetInt32();
                            }
                            else if (props.TryGetProperty("totalRetentionInDays", out var totalRetention) && totalRetention.ValueKind == JsonValueKind.Number)
                            {
                                retentionDays = totalRetention.GetInt32();
                            }
                        }

                        tableRetentions.Add(new TableRetentionInfo
                        {
                            TableName = tableName,
                            RetentionDays = retentionDays
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Não foi possível obter retenção de tabelas: {error}", ex.Message);
        }

        return tableRetentions;
    }

    private StandardFinding CreateFinding(
        string type,
        string resourceId,
        string name,
        string location,
        string resourceGroup,
        string subscriptionId,
        string priority,
        double confidence,
        string description,
        string recommendation,
        decimal dailyCost,
        decimal monthlyCost,
        decimal monthlySavings,
        Dictionary<string, object> metadata,
        Dictionary<string, string> tags)
    {
        return new StandardFinding
        {
            Type = type,
            ResourceId = resourceId,
            ResourceName = name,
            ResourceType = "Microsoft.OperationalInsights/workspaces",
            ResourceGroup = resourceGroup,
            Location = location,
            SubscriptionId = subscriptionId,
            DailyCost = dailyCost,
            EstimatedMonthlyCost = monthlyCost,
            EstimatedMonthlySavings = monthlySavings,
            Currency = "BRL",
            Priority = priority,
            Confidence = confidence,
            Description = description,
            Recommendation = recommendation,
            Tags = tags,
            Metadata = metadata
        };
    }

    private StandardAnalyzerResult CreateEmptyResult(string subscriptionId, int analysisPeriodDays, bool dryRun, string reason)
    {
        return new StandardAnalyzerResult
        {
            SchemaVersion = "1.0",
            AnalysisId = Guid.NewGuid().ToString(),
            Analyzer = AnalyzerNames.LOG_ANALYTICS_ANALYZER,
            SubscriptionId = subscriptionId,
            ExecutedAt = DateTime.UtcNow,
            AnalysisPeriodDays = analysisPeriodDays,
            DryRun = dryRun,
            Findings = new List<StandardFinding>(),
            ExecutionMetadata = new Dictionary<string, object>
            {
                { "skipped", true },
                { "reason", reason }
            }
        };
    }

    private Dictionary<string, string> ExtractTags(JsonElement resource)
    {
        var tags = new Dictionary<string, string>();

        if (resource.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in tagsElement.EnumerateObject())
            {
                tags[tag.Name] = tag.Value.GetString() ?? "";
            }
        }

        return tags;
    }

    // Classes auxiliares internas
    private class WorkspaceIngestionData
    {
        public decimal TotalGb { get; set; }
        public decimal DailyAverageGb { get; set; }
        public List<string> TopTables { get; set; } = new();
    }

    private class TableRetentionInfo
    {
        public string TableName { get; set; } = string.Empty;
        public int RetentionDays { get; set; }
    }
}
