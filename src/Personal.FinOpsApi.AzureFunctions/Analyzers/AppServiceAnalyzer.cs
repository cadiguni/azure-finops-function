using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;

namespace Personal.FinOpsApi.AzureFunctions.Analyzers;

/// <summary>
/// Analyzer para detectar App Services possivelmente ociosos
/// Analisa CPU + Requests para identificar Apps com baixa utilização
/// </summary>
public class AppServiceAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;
    private readonly AzureMetricsService _metricsService;
    private readonly HttpRetryService _httpRetryService;
    private readonly ResourceCostLookupService _costLookupService;
    private readonly ILogger<AppServiceAnalyzer> _logger;

    //  Tabela de preços aproximados (FALLBACK - usado quando Cost Management não retorna dados)
    private static readonly Dictionary<string, decimal> AppServicePlanPricesFallback = new()
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
        { "P1v3", 200m },    // Premium v3
        { "P2v2", 292m },    
        { "P2v3", 400m },
        { "P3v2", 584m },
        { "P3v3", 800m },
        { "Y1", 75m }        // Consumption (estimate)
    };

    public AppServiceAnalyzer(
        HttpClient httpClient, 
        AzureMetricsService metricsService,
        HttpRetryService httpRetryService,
        ResourceCostLookupService costLookupService,
        ILogger<AppServiceAnalyzer> logger)
    {
        _httpClient = httpClient;
        _credential = new DefaultAzureCredential();
        _metricsService = metricsService;
        _httpRetryService = httpRetryService;
        _costLookupService = costLookupService;
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
            _logger.LogInformation(" Token obtido com sucesso");

            // Pre-carregar custos do Cost Management para esta subscription
            await _costLookupService.PreloadCostsAsync(subscriptionId);

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

            //  Usar retry resiliente em vez de call direto
            var response = await _httpRetryService.PostWithRetryAsync(
                _httpClient,
                "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01",
                content);

            //  Tratamento especial para 429 persistente
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning(" Resource Graph API ainda rate-limited após retries - pulando análise");
                return new StandardAnalyzerResult(); // Retorna vazio em vez de falhar
            }

            response.EnsureSuccessStatusCode();
            var jsonResponse = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(jsonResponse);

            var appServicePlans = doc.RootElement.GetProperty("data").EnumerateArray().ToList();

            _logger.LogInformation(" Encontrados {count} App Service Plans", appServicePlans.Count);

            //  TIMEOUT GLOBAL: 5 minutos para toda a análise de App Services
            using var globalTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var plansAnalyzed = 0;
            var plansSkipped = 0;

            // Filtrar plans que não precisam de análise (F1, D1, Y1)
            var plansToAnalyze = new List<JsonElement>();
            foreach (var plan in appServicePlans)
            {
                var sku = plan.GetProperty("sku").GetString() ?? "B1";
                var skuTier = plan.GetProperty("skuTier").GetString() ?? "Basic";
                var name = plan.GetProperty("name").GetString() ?? "";

                if (sku.Equals("Y1", StringComparison.OrdinalIgnoreCase) || 
                    skuTier.Equals("Dynamic", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(" [SKIP] Plan '{name}' é Consumption (Y1/Dynamic) - Functions serverless não analisado para CPU/Memory", name);
                    plansSkipped++;
                    continue;
                }

                if (sku.Equals("F1", StringComparison.OrdinalIgnoreCase) || 
                    skuTier.Equals("Free", StringComparison.OrdinalIgnoreCase) ||
                    sku.Equals("D1", StringComparison.OrdinalIgnoreCase) ||
                    skuTier.Equals("Shared", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(" [SKIP] Plan '{name}' é Free/Shared ({sku}) - sem custo relevante para otimização", name, sku);
                    plansSkipped++;
                    continue;
                }

                plansToAnalyze.Add(plan);
            }

            _logger.LogInformation(" {toAnalyze} Plans para analisar, {skipped} skipped (F1/D1/Y1)", plansToAnalyze.Count, plansSkipped);

            //  PROCESSAMENTO PARALELO: até 5 Plans simultâneos para acelerar
            var findingsBag = new System.Collections.Concurrent.ConcurrentBag<StandardFinding>();
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 5,
                CancellationToken = globalTimeout.Token
            };

            try
            {
                await Parallel.ForEachAsync(plansToAnalyze, parallelOptions, async (plan, ct) =>
                {
                    var resourceId = plan.GetProperty("resourceId").GetString() ?? "";
                    var name = plan.GetProperty("name").GetString() ?? "";
                    var location = plan.GetProperty("location").GetString() ?? "";
                    var resourceGroup = plan.GetProperty("resourceGroup").GetString() ?? "";
                    var sku = plan.GetProperty("sku").GetString() ?? "B1";
                    var skuTier = plan.GetProperty("skuTier").GetString() ?? "Basic";

                    try
                    {
                        var finding = await AnalyzeSinglePlanAsync(subscriptionId, plan, analysisPeriodDays);
                        Interlocked.Increment(ref plansAnalyzed);

                        if (finding != null)
                        {
                            findingsBag.Add(finding);
                        }
                    }
                    catch (Exception planEx)
                    {
                        Interlocked.Increment(ref plansSkipped);
                        _logger.LogWarning(planEx, " [SKIP] Erro ao analisar Plan '{name}' - continuando", name);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(" [TIMEOUT] Análise de App Services interrompida após {analyzed} Plans (timeout global de 5 min)", plansAnalyzed);
            }

            findings.AddRange(findingsBag);

            _logger.LogInformation(" Análise App Services concluída: {findings} apps ociosos, {analyzed} Plans analisados, {skipped} skipped/erros", 
                findings.Count, plansAnalyzed, plansSkipped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro durante análise de App Services");
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
            _logger.LogWarning(" Validação falhou: {errors}", string.Join(", ", errors));
        }
        
        return result;
    }

    private decimal GetAppServicePlanCostFallback(string sku)
    {
        return AppServicePlanPricesFallback.GetValueOrDefault(sku, 100m); // Default R$100/mês
    }

    /// <summary>
    /// Analisa um único App Service Plan - busca métricas e retorna finding se subutilizado
    /// </summary>
    private async Task<StandardFinding?> AnalyzeSinglePlanAsync(string subscriptionId, JsonElement plan, int analysisPeriodDays)
    {
        var resourceId = plan.GetProperty("resourceId").GetString() ?? "";
        var name = plan.GetProperty("name").GetString() ?? "";
        var location = plan.GetProperty("location").GetString() ?? "";
        var resourceGroup = plan.GetProperty("resourceGroup").GetString() ?? "";
        var sku = plan.GetProperty("sku").GetString() ?? "B1";
        var skuTier = plan.GetProperty("skuTier").GetString() ?? "Basic";

        // 1. CPU do App Service Plan
        var avgCpuUsage = await _metricsService.GetAppServicePlanCpuAsync(resourceId, analysisPeriodDays);
        
        // 2. Memory % do App Service Plan
        var avgMemoryUsage = await _metricsService.GetAppServicePlanMemoryAsync(resourceId, analysisPeriodDays);
        var memoryAvailable = avgMemoryUsage >= 0;
        
        // 3. Descobrir Web Apps vinculados ao plan
        var webApps = await _metricsService.GetWebAppsInPlanAsync(resourceId);
        
        // 4. Requests totais dos Web Apps
        var avgRequests = await _metricsService.GetTotalRequestsAsync(webApps, analysisPeriodDays);

        if (memoryAvailable)
        {
            _logger.LogInformation(" Plan {name}: CPU={cpu:F1}%, Memory={memory:F1}%, Requests={requests}/h, WebApps={webAppCount} ({webAppStatus})", 
                name, avgCpuUsage, avgMemoryUsage, avgRequests, webApps.Count,
                webApps.Count == 0 ? "órfão" : "com apps");
        }
        else
        {
            _logger.LogInformation(" Plan {name}: CPU={cpu:F1}%, Memory=N/A, Requests={requests}/h, WebApps={webAppCount} ({webAppStatus})", 
                name, avgCpuUsage, avgRequests, webApps.Count,
                webApps.Count == 0 ? "órfão" : "com apps");
        }

        // Regras de detecção
        var reasonDetails = new List<string>();

        if (avgCpuUsage < 25.0)
            reasonDetails.Add($"CPU baixo: {avgCpuUsage:F1}%");
        
        if (memoryAvailable && avgMemoryUsage < 30.0)
            reasonDetails.Add($"Memória baixa: {avgMemoryUsage:F1}%");

        if (avgRequests < 200)
        {
            reasonDetails.Add(webApps.Count == 0 
                ? "Plan órfão (sem Web Apps)" 
                : $"Requests baixos: {avgRequests}/h");
        }

        if (webApps.Count == 0)
            reasonDetails.Add("Plan sem aplicações");

        var isUnderutilized = webApps.Count == 0 ||
                             (avgCpuUsage < 25.0 && avgRequests < 500) ||
                             avgCpuUsage < 15.0 ||
                             (memoryAvailable && avgCpuUsage < 25.0 && avgMemoryUsage < 30.0);

        if (!isUnderutilized)
            return null;

        // Custo real
        var costData = await _costLookupService.GetResourceCostDataAsync(subscriptionId, resourceId);
        var dailyCost = costData.DailyCost > 0 ? costData.DailyCost : GetAppServicePlanCostFallback(sku) / 30;
        var estimatedMonthlyCost = costData.MonthlyCost > 0 ? costData.MonthlyCost : GetAppServicePlanCostFallback(sku);
        var costSource = costData.MonthlyCost > 0 ? "cost-management" : "sku-fallback";
        var monthlySavings = estimatedMonthlyCost * 0.75m;

        return new StandardFinding
        {
            Type = FindingTypes.UNDERUTILIZED_APP_SERVICE,
            ResourceId = resourceId,
            ResourceName = name,
            ResourceType = "Microsoft.Web/serverfarms",
            ResourceGroup = resourceGroup,
            Location = location,
            SubscriptionId = subscriptionId,
            DailyCost = dailyCost,
            EstimatedMonthlyCost = estimatedMonthlyCost,
            EstimatedMonthlySavings = monthlySavings,
            Currency = "BRL",
            Priority = estimatedMonthlyCost > 200 ? FindingPriorities.HIGH : 
                      estimatedMonthlyCost > 100 ? FindingPriorities.MEDIUM : FindingPriorities.LOW,
            Confidence = webApps.Count == 0 ? 0.9 : 0.7,
            Description = $"App Service Plan '{name}' ({sku}) subutilizado há {analysisPeriodDays} dias: {string.Join(", ", reasonDetails)}. Apps vinculadas: {webApps.Count}",
            Recommendation = webApps.Count == 0 
                ? "Investigar Plan sem aplicações. Verificar se foi esvaziado intencionalmente ou se pode ser consolidado/removido."
                : avgCpuUsage < 5.0 
                    ? "Investigar possibilidade de migrar para um SKU menor (downgrade) ou consolidar com outros plans."
                    : "Investigar uso e avaliar otimização ou consolidação de plans.",
            Tags = ExtractTags(plan),
            Metadata = new Dictionary<string, object>
            {
                { "sku", sku },
                { "skuTier", skuTier },
                { "avgCpuUsage", avgCpuUsage },
                { "avgMemoryUsage", memoryAvailable ? avgMemoryUsage : -1 },
                { "memoryMetricAvailable", memoryAvailable },
                { "avgRequests", avgRequests },
                { "underutilizedDays", analysisPeriodDays },
                { "costSource", costSource },
                { "realCostFromApi", costData.MonthlyCost }
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
}