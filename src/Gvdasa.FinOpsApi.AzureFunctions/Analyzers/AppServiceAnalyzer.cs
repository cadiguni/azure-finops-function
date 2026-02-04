using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Gvdasa.FinOpsApi.AzureFunctions.Models;
using Gvdasa.FinOpsApi.AzureFunctions.Services;

namespace Gvdasa.FinOpsApi.AzureFunctions.Analyzers;

/// <summary>
/// Analyzer para detectar App Services possivelmente ociosos
/// Analisa CPU + Requests para identificar Apps com baixa utilização
/// </summary>
public class AppServiceAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;
    private readonly AzureMetricsService _metricsService;
    private readonly ILogger<AppServiceAnalyzer> _logger;

    // 💰 Tabela de preços aproximados (ordem de grandeza para FinOps) em BRL
    private static readonly Dictionary<string, decimal> AppServicePlanPrices = new()
    {
        { "F1", 0m },        // Free
        { "D1", 12m },       // Shared
        { "B1", 55m },       // Basic
        { "B2", 110m },      
        { "B3", 220m },      
        { "S1", 73m },       // Standard
        { "S2", 146m },      
        { "S3", 292m },      
        { "P1v2", 146m },    // Premium v2
        { "P2v2", 292m },    
        { "P3v2", 584m }
    };

    public AppServiceAnalyzer(
        HttpClient httpClient, 
        AzureMetricsService metricsService,
        ILogger<AppServiceAnalyzer> logger)
    {
        _httpClient = httpClient;
        _credential = new DefaultAzureCredential();
        _metricsService = metricsService;
        _logger = logger;
    }

    /// <summary>
    /// Analisa App Services subutilizados na subscription
    /// </summary>
    public async Task<StandardAnalyzerResult> AnalyzeAsync(string subscriptionId, int analysisPeriodDays = 7, bool dryRun = true)
    {
        var findings = new List<StandardFinding>();

        try
        {
            _logger.LogInformation("💽 Token obtido com sucesso");

            // Query KQL para encontrar App Service Plans
            var kqlQuery = $@"
                Resources
                | where type =~ 'microsoft.web/serverfarms'
                | where subscriptionId =~ '{subscriptionId}'
                | project
                    resourceId = id,
                    name,
                    resourceGroup,
                    subscriptionId,
                    location,
                    sku = sku.name,
                    skuTier = sku.tier,
                    capacity = sku.capacity,
                    kind,
                    tags
                ";

            var token = await _credential.GetTokenAsync(
                new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }));

            var resourceGraphPayload = new
            {
                query = kqlQuery
            };

            var jsonPayload = JsonSerializer.Serialize(resourceGraphPayload);
            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");

            var response = await _httpClient.PostAsync(
                "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01",
                content);

            response.EnsureSuccessStatusCode();
            var jsonResponse = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(jsonResponse);

            var appServicePlans = doc.RootElement.GetProperty("data").EnumerateArray().ToList();

            _logger.LogInformation("📊 Encontrados {count} App Service Plans", appServicePlans.Count);

            foreach (var plan in appServicePlans)
            {
                var resourceId = plan.GetProperty("resourceId").GetString() ?? "";
                var name = plan.GetProperty("name").GetString() ?? "";
                var location = plan.GetProperty("location").GetString() ?? "";
                var resourceGroup = plan.GetProperty("resourceGroup").GetString() ?? "";
                var sku = plan.GetProperty("sku").GetString() ?? "B1";
                var skuTier = plan.GetProperty("skuTier").GetString() ?? "Basic";

                // 📊 MÉTRICAS REAIS DO AZURE MONITOR
                _logger.LogDebug("📊 Buscando métricas reais para {resourceId}", resourceId);
                
                // 1. CPU do App Service Plan
                var avgCpuUsage = await _metricsService.GetAppServicePlanCpuAsync(resourceId, analysisPeriodDays);
                
                // 2. Descobrir Web Apps vinculados ao plan
                var webApps = await _metricsService.GetWebAppsInPlanAsync(resourceId);
                
                // 3. Requests totais dos Web Apps
                var avgRequests = await _metricsService.GetTotalRequestsAsync(webApps, analysisPeriodDays);
                
                _logger.LogInformation("📈 Plan {name}: CPU={cpu:F1}%, Requests={requests}/h, WebApps={webAppCount} ({webAppStatus})", 
                    name, avgCpuUsage, avgRequests, webApps.Count,
                    webApps.Count == 0 ? "órfão" : "com apps");

                // 🎯 Regras de detecção baseadas em métricas reais
                var isUnderutilized = false;
                var reasonDetails = new List<string>();

                // Regra 1: CPU baixo - mais realista para ambiente corporativo
                if (avgCpuUsage < 25.0) // Aumentado de 10% para 25%
                {
                    reasonDetails.Add($"CPU baixo: {avgCpuUsage:F1}%");
                }

                // Regra 2: Poucos requests (< 500/h agora é considerado baixo)
                // ✅ Com Resource Graph, agora temos Web Apps reais e requests reais
                if (avgRequests < 500) // Aumentado de 100 para 500
                {
                    var requestsMessage = webApps.Count == 0 
                        ? "Plan órfão (sem Web Apps)" 
                        : $"Requests baixos: {avgRequests}/h";
                    reasonDetails.Add(requestsMessage);
                }

                // Regra 3: Plan sem Web Apps (órfão)
                if (webApps.Count == 0)
                {
                    reasonDetails.Add("Plan sem aplicações");
                }

                // 🧠 Lógica mais inteligente de detecção:
                // - Plan órfão = sempre subutilizado
                // - CPU baixo (< 25%) + poucos requests (< 500/h) = subutilizado 
                // - CPU muito baixo (< 15%) mesmo com requests = subutilizado
                isUnderutilized = webApps.Count == 0 || // Plan órfão
                                 (avgCpuUsage < 25.0 && avgRequests < 500) || // Ambos baixos
                                 avgCpuUsage < 15.0; // CPU muito baixo independente

                if (isUnderutilized)
                {
                    var estimatedMonthlyCost = GetAppServicePlanCost(sku);
                    var monthlySavings = estimatedMonthlyCost * 0.75m; // 75% economia ao otimizar

                    var finding = new StandardFinding
                    {
                        Type = FindingTypes.UNDERUTILIZED_APP_SERVICE,
                        ResourceId = resourceId,
                        ResourceName = name,
                        ResourceType = "Microsoft.Web/serverfarms",
                        ResourceGroup = resourceGroup,  // ✅ CORRIGIDO: Campo obrigatório
                        Location = location,            // ✅ CORRIGIDO: Campo obrigatório  
                        SubscriptionId = subscriptionId,
                        EstimatedMonthlyCost = estimatedMonthlyCost,
                        EstimatedMonthlySavings = monthlySavings,
                        Currency = "BRL",
                        Priority = estimatedMonthlyCost > 200 ? FindingPriorities.HIGH : 
                                  estimatedMonthlyCost > 100 ? FindingPriorities.MEDIUM : FindingPriorities.LOW,
                        Confidence = webApps.Count == 0 ? 0.9 : 0.7, // Mais confiança em plans órfãos
                        Description = $"App Service Plan '{name}' ({sku}) subutilizado há {analysisPeriodDays} dias: {string.Join(", ", reasonDetails)}. Apps vinculadas: {webApps.Count}",
                        Recommendation = webApps.Count == 0 
                            ? "Plan sem aplicações - considere remover para economizar custos."
                            : avgCpuUsage < 5.0 
                                ? "Considere migrar para um SKU menor (downgrade) ou consolidar com outros plans."
                                : "Monitore uso e considere otimização ou consolidação.",
                        Tags = ExtractTags(plan),       // ✅ CORRIGIDO: Campo no lugar certo
                        Metadata = new Dictionary<string, object>
                        {
                            { "sku", sku },
                            { "skuTier", skuTier },
                            { "avgCpuUsage", avgCpuUsage },
                            { "avgRequests", avgRequests },
                            { "underutilizedDays", analysisPeriodDays }
                        }
                    };

                    findings.Add(finding);
                }
            }

            _logger.LogInformation("✅ Análise App Services concluída: {count} apps ociosos encontrados", findings.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro durante análise de App Services");
        }

        var result = new StandardAnalyzerResult
        {
            SchemaVersion = "1.0",
            AnalysisId = Guid.NewGuid().ToString(),
            Analyzer = AnalyzerNames.APP_SERVICE_ANALYZER,
            SubscriptionId = subscriptionId,
            ExecutedAt = DateTime.UtcNow,
            AnalysisPeriodDays = analysisPeriodDays,
            DryRun = dryRun,
            Findings = findings,
            ExecutionMetadata = new Dictionary<string, object>
            {
                { "totalResourcesAnalyzed", findings.Count },
                { "analyzerVersion", "2.0" },
                { "cpuThreshold", 10.0 },
                { "requestThreshold", 100 }
            }
        };
        
        var (isValid, errors) = AnalyzerContractValidator.ValidateResult(result);
        if (!isValid)
        {
            _logger.LogWarning("⚠️ Validação falhou: {errors}", string.Join(", ", errors));
        }
        
        return result;
    }

    private decimal GetAppServicePlanCost(string sku)
    {
        return AppServicePlanPrices.GetValueOrDefault(sku, 100m); // Default R$100/mês
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