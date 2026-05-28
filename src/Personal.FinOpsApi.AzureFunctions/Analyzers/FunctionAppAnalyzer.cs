using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;

namespace Personal.FinOpsApi.AzureFunctions.Analyzers;

/// <summary>
/// Analyzer para detectar Function Apps possivelmente órfãs ou subutilizadas
/// Foco em planos Consumption (Y1) e Elastic Premium (EP)
/// Analisa FunctionExecutionCount e FunctionExecutionUnits
/// </summary>
public class FunctionAppAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;
    private readonly AzureMetricsService _metricsService;
    private readonly HttpRetryService _httpRetryService;
    private readonly ResourceCostLookupService _costLookupService;
    private readonly ILogger<FunctionAppAnalyzer> _logger;

    public FunctionAppAnalyzer(
        HttpClient httpClient, 
        AzureMetricsService metricsService,
        HttpRetryService httpRetryService,
        ResourceCostLookupService costLookupService,
        ILogger<FunctionAppAnalyzer> logger)
    {
        _httpClient = httpClient;
        _credential = new DefaultAzureCredential();
        _metricsService = metricsService;
        _httpRetryService = httpRetryService;
        _costLookupService = costLookupService;
        _logger = logger;
    }

    /// <summary>
    /// Analisa Function Apps na subscription
    /// Identifica Functions sem execuções recentes (órfãs)
    /// </summary>
    public async Task<StandardAnalyzerResult> AnalyzeAsync(string subscriptionId, int analysisPeriodDays = 7, bool dryRun = true)
    {
        var findings = new List<StandardFinding>();

        try
        {
            _logger.LogInformation("🔍 [FUNCTION-ANALYZER] Iniciando análise de Function Apps para {subscriptionId}", subscriptionId);

            // Pre-carregar custos do Cost Management para esta subscription
            await _costLookupService.PreloadCostsAsync(subscriptionId);

            // Query KQL para encontrar Function Apps (kind contains "functionapp")
            var kqlQuery = $@"
                Resources
                | where type =~ 'microsoft.web/sites'
                | where kind contains 'functionapp'
                | where subscriptionId =~ '{subscriptionId}'
                | project
                    resourceId = id,
                    name,
                    resourceGroup,
                    subscriptionId,
                    location,
                    kind,
                    serverFarmId = properties.serverFarmId,
                    state = properties.state,
                    lastModifiedTimeUtc = properties.lastModifiedTimeUtc,
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

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("⚠️ [FUNCTION-ANALYZER] Resource Graph API rate-limited - pulando análise");
                return new StandardAnalyzerResult();
            }

            response.EnsureSuccessStatusCode();
            var jsonResponse = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(jsonResponse);

            var functionApps = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
            _logger.LogInformation("📦 [FUNCTION-ANALYZER] Encontradas {count} Function Apps", functionApps.Count);

            foreach (var funcApp in functionApps)
            {
                var resourceId = funcApp.GetProperty("resourceId").GetString() ?? "";
                var name = funcApp.GetProperty("name").GetString() ?? "";
                var location = funcApp.GetProperty("location").GetString() ?? "";
                var resourceGroup = funcApp.GetProperty("resourceGroup").GetString() ?? "";
                var kind = funcApp.GetProperty("kind").GetString() ?? "";
                var state = funcApp.TryGetProperty("state", out var stateEl) ? stateEl.GetString() ?? "Unknown" : "Unknown";

                _logger.LogDebug("🔍 Analisando Function App: {name} (kind: {kind}, state: {state})", name, kind, state);

                // Buscar métricas de execução do Azure Monitor
                var executionMetrics = await _metricsService.GetFunctionAppExecutionMetricsAsync(resourceId, analysisPeriodDays);

                var totalExecutions = executionMetrics.TotalExecutions;
                var avgExecutionsPerDay = executionMetrics.AvgExecutionsPerDay;
                var totalExecutionUnits = executionMetrics.TotalExecutionUnits;
                var hasRecentActivity = totalExecutions > 0;

                _logger.LogInformation("📊 Function {name}: Execuções={executions} ({avgPerDay}/dia), Units={units}", 
                    name, totalExecutions, avgExecutionsPerDay, totalExecutionUnits);

                // Regras de detecção para Functions
                var isUnderutilized = false;
                var reasonDetails = new List<string>();

                // Regra 1: Nenhuma execução no período (Function órfã)
                if (totalExecutions == 0)
                {
                    reasonDetails.Add($"Sem execuções há {analysisPeriodDays} dias");
                    isUnderutilized = true;
                }
                // Regra 2: Muito poucas execuções (< 10/dia em média)
                else if (avgExecutionsPerDay < 10)
                {
                    reasonDetails.Add($"Poucas execuções: {avgExecutionsPerDay:F1}/dia");
                    isUnderutilized = true;
                }

                // Regra 3: Estado parado
                if (state.Equals("Stopped", StringComparison.OrdinalIgnoreCase))
                {
                    reasonDetails.Add("Function App está parada");
                    isUnderutilized = true;
                }

                if (isUnderutilized)
                {
                    // 💰 Buscar custo real do Cost Management
                    var costData = await _costLookupService.GetResourceCostDataAsync(subscriptionId, resourceId);
                    var dailyCost = costData.DailyCost;
                    var estimatedMonthlyCost = costData.MonthlyCost;
                    var costSource = costData.MonthlyCost > 0 ? "cost-management" : "no-cost-data";
                    
                    // Para Functions Consumption sem execução, custo é praticamente zero
                    var monthlySavings = estimatedMonthlyCost * 0.9m; // 90% economia potencial

                    var priority = totalExecutions == 0 
                        ? FindingPriorities.MEDIUM  // Órfã = investigar
                        : FindingPriorities.LOW;    // Poucas execuções = baixa prioridade

                    var finding = new StandardFinding
                    {
                        Type = FindingTypes.UNDERUTILIZED_FUNCTION_APP,
                        ResourceId = resourceId,
                        ResourceName = name,
                        ResourceType = "Microsoft.Web/sites (FunctionApp)",
                        ResourceGroup = resourceGroup,
                        Location = location,
                        SubscriptionId = subscriptionId,
                        DailyCost = dailyCost,
                        EstimatedMonthlyCost = estimatedMonthlyCost,
                        EstimatedMonthlySavings = monthlySavings,
                        Currency = "BRL",
                        Priority = priority,
                        Confidence = totalExecutions == 0 ? 0.9 : 0.6, // Alta confiança se zero execuções
                        Description = $"Function App '{name}' possivelmente órfã há {analysisPeriodDays} dias: {string.Join(", ", reasonDetails)}",
                        Recommendation = "Investigar se a Function ainda é necessária. Verificar triggers configurados e se há dependências ativas.",
                        Tags = ExtractTags(funcApp),
                        Metadata = new Dictionary<string, object>
                        {
                            { "kind", kind },
                            { "state", state },
                            { "totalExecutions", totalExecutions },
                            { "avgExecutionsPerDay", avgExecutionsPerDay },
                            { "totalExecutionUnits", totalExecutionUnits },
                            { "analysisPeriodDays", analysisPeriodDays },
                            { "costSource", costSource },
                            { "hasRecentActivity", hasRecentActivity }
                        }
                    };

                    findings.Add(finding);
                }
            }

            _logger.LogInformation("✅ [FUNCTION-ANALYZER] Análise concluída: {count} Functions possivelmente órfãs", findings.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [FUNCTION-ANALYZER] Erro durante análise de Function Apps");
        }

        var result = new StandardAnalyzerResult
        {
            SchemaVersion = "1.0",
            AnalysisId = Guid.NewGuid().ToString(),
            Analyzer = "FunctionAppAnalyzer",
            SubscriptionId = subscriptionId,
            ExecutedAt = DateTime.UtcNow,
            AnalysisPeriodDays = analysisPeriodDays,
            DryRun = dryRun,
            Findings = findings,
            ExecutionMetadata = new Dictionary<string, object>
            {
                { "totalResourcesAnalyzed", findings.Count },
                { "analyzerVersion", "1.0" },
                { "executionThreshold", 10 }
            }
        };

        return result;
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
}
