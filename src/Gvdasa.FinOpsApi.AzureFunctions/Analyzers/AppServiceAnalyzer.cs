using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Gvdasa.FinOpsApi.AzureFunctions.Models;

namespace Gvdasa.FinOpsApi.AzureFunctions.Analyzers;

/// <summary>
/// Analyzer para detectar App Services possivelmente ociosos
/// Analisa CPU + Requests para identificar Apps com baixa utilização
/// </summary>
public class AppServiceAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;
    private readonly ILogger<AppServiceAnalyzer> _logger;

    // 💰 Tabela de preços aproximados (ordem de grandeza para FinOps)
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
        { "P3v2", 584m },    
        { "P1v3", 365m },    // Premium v3
        { "P2v3", 730m },    
        { "P3v3", 1460m },   
        { "I1v2", 584m },    // Isolated v2
        { "I2v2", 1168m },   
        { "I3v2", 2336m }    
    };

    public AppServiceAnalyzer(HttpClient httpClient, ILogger<AppServiceAnalyzer> logger)
    {
        _httpClient = httpClient;
        _credential = new DefaultAzureCredential();
        _logger = logger;
    }

    /// <summary>
    /// Analisa App Services ociosos na subscription
    /// </summary>
    public async Task<List<CostRecommendation>> AnalyzeAsync(string subscriptionId)
    {
        var recommendations = new List<CostRecommendation>();

        try
        {
            _logger.LogInformation("🌐 Iniciando análise de App Services ociosos...");

            // 1️⃣ Buscar App Service Plans
            var appServicePlans = await GetAppServicePlansAsync(subscriptionId);
            _logger.LogInformation("📊 Encontrados {count} App Service Plans", appServicePlans.Count);

            // 2️⃣ Para cada plano, analisar seus Apps
            foreach (var plan in appServicePlans)
            {
                try
                {
                    var planName = plan.GetProperty("name").GetString() ?? "";
                    var planId = plan.GetProperty("id").GetString() ?? "";
                    
                    _logger.LogDebug("🔍 Analisando App Service Plan: {planName}", planName);

                    // Buscar Apps do plano
                    var apps = await GetAppServicesFromPlanAsync(subscriptionId, planId);
                    
                    if (apps.Count == 0)
                    {
                        _logger.LogDebug("⏭️ Plan {planName} não tem apps", planName);
                        continue;
                    }

                    // Analisar cada app
                    foreach (var app in apps)
                    {
                        var appRecommendation = await AnalyzeAppServiceAsync(app, plan, apps.Count, subscriptionId);
                        if (appRecommendation != null)
                        {
                            recommendations.Add(appRecommendation);
                            _logger.LogInformation("💡 App Service ocioso detectado: {appName} (R$ {savings}/mês)", 
                                appRecommendation.ResourceName, appRecommendation.EstimatedMonthlySavings);
                        }
                    }
                }
                catch (Exception ex)
                {
                    var planName = plan.GetProperty("name").GetString() ?? "unknown";
                    _logger.LogWarning(ex, "⚠️ Erro ao analisar App Service Plan {planName}", planName);
                }
            }

            _logger.LogInformation("✅ Análise App Services concluída: {count} apps ociosos encontrados", recommendations.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro geral na análise de App Services");
            throw;
        }

        return recommendations;
    }

    /// <summary>
    /// Busca App Service Plans na subscription
    /// </summary>
    private async Task<List<JsonElement>> GetAppServicePlansAsync(string subscriptionId)
    {
        var kqlQuery = @"
            Resources
            | where type == 'microsoft.web/serverfarms'
            | project id, name, resourceGroup, location, tags, sku, kind";

        return await ExecuteResourceGraphQueryAsync(kqlQuery, subscriptionId);
    }

    /// <summary>
    /// Busca App Services (sites) de um App Service Plan específico
    /// </summary>
    private async Task<List<JsonElement>> GetAppServicesFromPlanAsync(string subscriptionId, string planId)
    {
        var kqlQuery = $@"
            Resources
            | where type == 'microsoft.web/sites'
            | where tostring(properties.serverFarmId) == '{planId}'
            | project
                id,
                name,
                resourceGroup,
                location,
                tags,
                serverFarmId = tostring(properties.serverFarmId),
                appKind = tostring(kind),
                state = tostring(properties.state)";

        return await ExecuteResourceGraphQueryAsync(kqlQuery, subscriptionId);
    }

    /// <summary>
    /// Analisa um App Service específico
    /// </summary>
    private async Task<CostRecommendation?> AnalyzeAppServiceAsync(JsonElement app, JsonElement plan, int appsInPlan, string subscriptionId)
    {
        try
        {
            var appName = app.GetProperty("name").GetString() ?? "";
            var appId = app.GetProperty("id").GetString() ?? "";
            var appState = app.GetProperty("state").GetString() ?? "";

            // Pular se app está parado
            if (appState.ToLower() != "running")
            {
                _logger.LogDebug("⏭️ App {appName} não está em execução (state: {state})", appName, appState);
                return null;
            }

            // Verificar se deve pular baseado em tags
            if (ShouldSkipApp(app))
            {
                _logger.LogDebug("⏭️ App {appName} ignorado (tags especiais)", appName);
                return null;
            }

            // Coletar métricas dos últimos 30 dias
            var metrics = await GetAppServiceMetricsAsync(subscriptionId, appId);

            // Aplicar regras de decisão
            if (IsAppServiceIdle(metrics))
            {
                return CreateAppServiceRecommendation(app, plan, appsInPlan, metrics, subscriptionId);
            }

            return null;
        }
        catch (Exception ex)
        {
            var appName = app.GetProperty("name").GetString() ?? "unknown";
            _logger.LogWarning(ex, "⚠️ Erro ao analisar App Service {appName}", appName);
            return null;
        }
    }

    /// <summary>
    /// Coleta métricas de CPU e Requests do App Service
    /// </summary>
    private async Task<AppServiceMetrics> GetAppServiceMetricsAsync(string subscriptionId, string resourceId)
    {
        var accessToken = await _credential.GetTokenAsync(new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }));
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

        // Período: últimos 30 dias
        var endTime = DateTime.UtcNow;
        var startTime = endTime.AddDays(-30);
        var timespan = $"{startTime:yyyy-MM-ddTHH:mm:ss.fffZ}/{endTime:yyyy-MM-ddTHH:mm:ss.fffZ}";

        // 📊 Coletar métricas essenciais
        var cpuAvg = await GetMetricAverageAsync(subscriptionId, resourceId, "CpuPercentage", timespan);
        var requestsTotal = await GetMetricTotalAsync(subscriptionId, resourceId, "Requests", timespan);

        return new AppServiceMetrics
        {
            CpuAveragePercent = cpuAvg,
            RequestsTotal = requestsTotal
        };
    }

    /// <summary>
    /// Coleta uma métrica específica e retorna a média
    /// </summary>
    private async Task<double> GetMetricAverageAsync(string subscriptionId, string resourceId, string metricName, string timespan)
    {
        try
        {
            var url = $"https://management.azure.com{resourceId}/providers/Microsoft.Insights/metrics" +
                     $"?api-version=2018-01-01" +
                     $"&metricnames={Uri.EscapeDataString(metricName)}" +
                     $"&timespan={timespan}" +
                     $"&interval=P1D" +
                     $"&aggregation=Average";

            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("⚠️ Erro ao buscar métrica {metric}: {status}", metricName, response.StatusCode);
                return 0;
            }

            var content = await response.Content.ReadAsStringAsync();
            var metricsData = JsonSerializer.Deserialize<JsonElement>(content);

            return ExtractAverageFromMetrics(metricsData);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Erro ao processar métrica {metric}", metricName);
            return 0;
        }
    }

    /// <summary>
    /// Coleta uma métrica específica e retorna o total
    /// </summary>
    private async Task<double> GetMetricTotalAsync(string subscriptionId, string resourceId, string metricName, string timespan)
    {
        try
        {
            var url = $"https://management.azure.com{resourceId}/providers/Microsoft.Insights/metrics" +
                     $"?api-version=2018-01-01" +
                     $"&metricnames={Uri.EscapeDataString(metricName)}" +
                     $"&timespan={timespan}" +
                     $"&interval=P1D" +
                     $"&aggregation=Total";

            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("⚠️ Erro ao buscar métrica {metric}: {status}", metricName, response.StatusCode);
                return 0;
            }

            var content = await response.Content.ReadAsStringAsync();
            var metricsData = JsonSerializer.Deserialize<JsonElement>(content);

            return ExtractTotalFromMetrics(metricsData);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Erro ao processar métrica {metric}", metricName);
            return 0;
        }
    }

    /// <summary>
    /// Extrai valor médio dos dados de métricas
    /// </summary>
    private double ExtractAverageFromMetrics(JsonElement metricsData)
    {
        if (metricsData.TryGetProperty("value", out var metricsArray) && metricsArray.GetArrayLength() > 0)
        {
            var firstMetric = metricsArray[0];
            if (firstMetric.TryGetProperty("timeseries", out var timeseries) && timeseries.GetArrayLength() > 0)
            {
                var data = timeseries[0].GetProperty("data");
                var values = new List<double>();

                foreach (var dataPoint in data.EnumerateArray())
                {
                    if (dataPoint.TryGetProperty("average", out var avgValue) && avgValue.ValueKind != JsonValueKind.Null)
                    {
                        values.Add(avgValue.GetDouble());
                    }
                }

                return values.Count > 0 ? values.Average() : 0;
            }
        }

        return 0;
    }

    /// <summary>
    /// Extrai valor total dos dados de métricas
    /// </summary>
    private double ExtractTotalFromMetrics(JsonElement metricsData)
    {
        if (metricsData.TryGetProperty("value", out var metricsArray) && metricsArray.GetArrayLength() > 0)
        {
            var firstMetric = metricsArray[0];
            if (firstMetric.TryGetProperty("timeseries", out var timeseries) && timeseries.GetArrayLength() > 0)
            {
                var data = timeseries[0].GetProperty("data");
                double total = 0;

                foreach (var dataPoint in data.EnumerateArray())
                {
                    if (dataPoint.TryGetProperty("total", out var totalValue) && totalValue.ValueKind != JsonValueKind.Null)
                    {
                        total += totalValue.GetDouble();
                    }
                }

                return total;
            }
        }

        return 0;
    }

    /// <summary>
    /// Verifica se o App Service deve ser ignorado baseado em tags
    /// </summary>
    private bool ShouldSkipApp(JsonElement app)
    {
        if (!app.TryGetProperty("tags", out var tagsElement) || tagsElement.ValueKind != JsonValueKind.Object)
            return false;

        var tags = new Dictionary<string, string>();
        foreach (var tag in tagsElement.EnumerateObject())
        {
            tags[tag.Name.ToLower()] = tag.Value.GetString()?.ToLower() ?? "";
        }

        // ⚠️ Regras de exclusão
        if (tags.ContainsKey("alwayson") && tags["alwayson"] == "true")
            return true;

        if (tags.ContainsKey("environment") && tags["environment"] == "prod")
            return true;

        if (tags.ContainsKey("critical") && tags["critical"] == "true")
            return true;

        return false;
    }

    /// <summary>
    /// Aplica regras de decisão: App Service está ocioso?
    /// </summary>
    private bool IsAppServiceIdle(AppServiceMetrics metrics)
    {
        // 🎯 Critérios conservadores e seguros
        const double CPU_THRESHOLD = 5.0;     // CPU < 5%
        const double REQUESTS_THRESHOLD = 0;  // Requests == 0

        return metrics.CpuAveragePercent < CPU_THRESHOLD &&
               metrics.RequestsTotal <= REQUESTS_THRESHOLD;
    }

    /// <summary>
    /// Cria recomendação para App Service ocioso
    /// </summary>
    private CostRecommendation CreateAppServiceRecommendation(JsonElement app, JsonElement plan, int appsInPlan, AppServiceMetrics metrics, string subscriptionId)
    {
        var appId = app.GetProperty("id").GetString() ?? "";
        var appName = app.GetProperty("name").GetString() ?? "";
        var resourceGroup = app.GetProperty("resourceGroup").GetString() ?? "";
        
        // Extrair SKU do objeto sku
        var planSku = "";
        if (plan.TryGetProperty("sku", out var skuElement) && skuElement.TryGetProperty("name", out var skuNameElement))
        {
            planSku = skuNameElement.GetString() ?? "";
        }

        // 💰 Calcular economia baseada no App Service Plan
        var planCost = EstimateAppServicePlanCost(planSku);
        var estimatedSavings = appsInPlan == 1 ? planCost : planCost / appsInPlan; // Dividir proporcionalmente

        return new CostRecommendation
        {
            Type = "UnderUtilizedAppService",
            ResourceId = appId,
            ResourceName = appName,
            ResourceType = "Microsoft.Web/sites",
            ResourceGroup = resourceGroup,
            SubscriptionId = subscriptionId,
            EstimatedMonthlySavings = estimatedSavings,
            Priority = estimatedSavings > 200 ? "High" : estimatedSavings > 50 ? "Medium" : "Low",
            Description = $"App Service '{appName}' apresenta uso mínimo (CPU {metrics.CpuAveragePercent:F1}% e {metrics.RequestsTotal} requests) nos últimos 30 dias. " +
                         $"Avaliar remoção, consolidação ou downgrade do App Service Plan ({planSku}). Economia estimada: R$ {estimatedSavings:F2}/mês.",
            Tags = ExtractTags(app)
        };
    }

    /// <summary>
    /// Estima custo mensal do App Service Plan baseado no SKU
    /// </summary>
    private decimal EstimateAppServicePlanCost(string sku)
    {
        if (AppServicePlanPrices.TryGetValue(sku, out var price))
        {
            return price;
        }

        // Valor padrão para SKUs desconhecidos baseado no padrão do nome
        return sku.ToUpper() switch
        {
            var s when s.StartsWith("F") => 0m,      // Free
            var s when s.StartsWith("D") => 12m,     // Shared
            var s when s.StartsWith("B") => 100m,    // Basic
            var s when s.StartsWith("S") => 150m,    // Standard
            var s when s.StartsWith("P") => 400m,    // Premium
            var s when s.StartsWith("I") => 800m,    // Isolated
            _ => 75m                                  // Padrão conservador
        };
    }

    /// <summary>
    /// Executa query no Azure Resource Graph
    /// </summary>
    private async Task<List<JsonElement>> ExecuteResourceGraphQueryAsync(string kqlQuery, string subscriptionId)
    {
        try
        {
            var accessToken = await _credential.GetTokenAsync(new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }));
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

            var requestBody = new
            {
                subscriptions = new[] { subscriptionId },
                query = kqlQuery
            };

            var jsonContent = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01", content);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<JsonElement>(responseContent);
                
                if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    return data.EnumerateArray().ToList();
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("❌ Resource Graph query failed: {status} - {error}", response.StatusCode, errorContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao executar Resource Graph query");
        }

        return new List<JsonElement>();
    }

    /// <summary>
    /// Extrai tags do recurso
    /// </summary>
    private Dictionary<string, string> ExtractTags(JsonElement app)
    {
        var tags = new Dictionary<string, string>();
        
        if (app.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in tagsElement.EnumerateObject())
            {
                tags[tag.Name] = tag.Value.GetString() ?? "";
            }
        }

        return tags;
    }
}

/// <summary>
/// Métricas coletadas do App Service
/// </summary>
public class AppServiceMetrics
{
    public double CpuAveragePercent { get; set; }
    public double RequestsTotal { get; set; }
}