using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Gvdasa.FinOpsApi.AzureFunctions.Models;

namespace Gvdasa.FinOpsApi.AzureFunctions.Analyzers;

/// <summary>
/// Analyzer para detectar VMs ligadas mas ociosas (idle)
/// Maior impacto financeiro na plataforma FinOps
/// </summary>
public class IdleVmAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;
    private readonly ILogger<IdleVmAnalyzer> _logger;

    public IdleVmAnalyzer(HttpClient httpClient, ILogger<IdleVmAnalyzer> logger)
    {
        _httpClient = httpClient;
        _credential = new DefaultAzureCredential();
        _logger = logger;
    }

    /// <summary>
    /// Analisa VMs ociosas na subscription
    /// Combina Resource Graph + Azure Monitor Metrics
    /// </summary>
    public async Task<List<CostRecommendation>> AnalyzeAsync(string subscriptionId)
    {
        var recommendations = new List<CostRecommendation>();

        try
        {
            _logger.LogInformation("🖥️ Iniciando análise de VMs ociosas - o maior impacto FinOps...");

            // 1️⃣ Buscar VMs ligadas via Resource Graph
            var runningVms = await GetRunningVmsAsync(subscriptionId);
            _logger.LogInformation("🔍 Encontradas {count} VMs em execução", runningVms.Count);

            // 2️⃣ Para cada VM, coletar métricas + análise
            foreach (var vm in runningVms)
            {
                try
                {
                    var vmName = vm.GetProperty("name").GetString() ?? "";
                    var resourceId = vm.GetProperty("id").GetString() ?? "";
                    
                    _logger.LogDebug("📊 Analisando métricas da VM: {vmName}", vmName);

                    // Verificar se deve pular esta VM
                    if (ShouldSkipVm(vm))
                    {
                        _logger.LogDebug("⏭️ VM {vmName} ignorada (tags especiais)", vmName);
                        continue;
                    }

                    // Coletar métricas dos últimos 7 dias
                    var metrics = await GetVmMetricsAsync(subscriptionId, resourceId);
                    
                    // Aplicar regras de decisão
                    if (IsVmIdle(metrics))
                    {
                        var recommendation = CreateIdleVmRecommendation(vm, metrics, subscriptionId);
                        if (recommendation != null)
                        {
                            recommendations.Add(recommendation);
                            _logger.LogInformation("💡 VM ociosa detectada: {vmName} (R$ {savings}/mês)", 
                                vmName, recommendation.EstimatedMonthlySavings);
                        }
                    }
                }
                catch (Exception ex)
                {
                    var vmName = vm.GetProperty("name").GetString() ?? "unknown";
                    _logger.LogWarning(ex, "⚠️ Erro ao analisar VM {vmName}: {error}", vmName, ex.Message);
                }
            }

            _logger.LogInformation("✅ Análise VMs concluída: {count} VMs ociosas encontradas", recommendations.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro geral na análise de VMs ociosas");
            throw;
        }

        return recommendations;
    }

    /// <summary>
    /// Busca VMs em estado Running via Resource Graph
    /// </summary>
    private async Task<List<JsonElement>> GetRunningVmsAsync(string subscriptionId)
    {
        var kqlQuery = @"
            Resources
            | where type == 'microsoft.compute/virtualmachines'
            | extend powerState = tostring(properties.extended.instanceView.powerState.code)
            | where powerState == 'PowerState/running'
            | project id, name, resourceGroup, location, tags, 
                      sku = properties.hardwareProfile.vmSize,
                      powerState";

        return await ExecuteResourceGraphQueryAsync(kqlQuery, subscriptionId);
    }

    /// <summary>
    /// Coleta métricas de CPU e Network da VM via Azure Monitor
    /// </summary>
    private async Task<VmMetrics> GetVmMetricsAsync(string subscriptionId, string resourceId)
    {
        var accessToken = await _credential.GetTokenAsync(new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }));
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

        // Período: últimos 7 dias
        var endTime = DateTime.UtcNow;
        var startTime = endTime.AddDays(-7);
        var timespan = $"{startTime:yyyy-MM-ddTHH:mm:ss.fffZ}/{endTime:yyyy-MM-ddTHH:mm:ss.fffZ}";

        // 📊 Coletar 3 métricas essenciais
        var cpuAvg = await GetMetricAverageAsync(subscriptionId, resourceId, "Percentage CPU", timespan);
        var networkIn = await GetMetricAverageAsync(subscriptionId, resourceId, "Network In Total", timespan);
        var networkOut = await GetMetricAverageAsync(subscriptionId, resourceId, "Network Out Total", timespan);

        return new VmMetrics
        {
            CpuAveragePercent = cpuAvg,
            NetworkInBytes = networkIn,
            NetworkOutBytes = networkOut
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
                     $"&interval=PT1H" +
                     $"&aggregation=Average";

            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("⚠️ Erro ao buscar métrica {metric}: {status}", metricName, response.StatusCode);
                return 0;
            }

            var content = await response.Content.ReadAsStringAsync();
            var metricsData = JsonSerializer.Deserialize<JsonElement>(content);

            // Extrair valores das métricas
            if (metricsData.TryGetProperty("value", out var metricsArray) && metricsArray.GetArrayLength() > 0)
            {
                var firstMetric = metricsArray[0];
                if (firstMetric.TryGetProperty("timeseries", out var timeseries) && timeseries.GetArrayLength() > 0)
                {
                    var data = timeseries[0].GetProperty("data");
                    var values = new List<double>();

                    foreach (var dataPoint in data.EnumerateArray())
                    {
                        if (dataPoint.TryGetProperty("average", out var avgValue))
                        {
                            values.Add(avgValue.GetDouble());
                        }
                    }

                    return values.Count > 0 ? values.Average() : 0;
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Erro ao processar métrica {metric}", metricName);
            return 0;
        }
    }

    /// <summary>
    /// Verifica se a VM deve ser ignorada baseada em tags
    /// </summary>
    private bool ShouldSkipVm(JsonElement vm)
    {
        if (!vm.TryGetProperty("tags", out var tagsElement) || tagsElement.ValueKind != JsonValueKind.Object)
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

        if (tags.ContainsKey("role") && (tags["role"].Contains("backup") || tags["role"].Contains("jumpbox")))
            return true;

        return false;
    }

    /// <summary>
    /// Aplica regras de decisão: VM está ociosa?
    /// </summary>
    private bool IsVmIdle(VmMetrics metrics)
    {
        // 🎯 Critérios simples e confiáveis
        const double CPU_THRESHOLD = 2.0; // CPU < 2%
        const double NETWORK_THRESHOLD = 100 * 1024; // Network < 100KB (quase zero)

        return metrics.CpuAveragePercent < CPU_THRESHOLD &&
               metrics.NetworkInBytes < NETWORK_THRESHOLD &&
               metrics.NetworkOutBytes < NETWORK_THRESHOLD;
    }

    /// <summary>
    /// Cria recomendação para VM ociosa
    /// </summary>
    private CostRecommendation CreateIdleVmRecommendation(JsonElement vm, VmMetrics metrics, string subscriptionId)
    {
        var resourceId = vm.GetProperty("id").GetString() ?? "";
        var vmName = vm.GetProperty("name").GetString() ?? "";
        var resourceGroup = vm.GetProperty("resourceGroup").GetString() ?? "";
        var vmSize = vm.GetProperty("sku").GetString() ?? "";

        // 💸 Estimativa de custo baseada no SKU da VM
        var estimatedMonthlyCost = EstimateVmMonthlyCost(vmSize);

        return new CostRecommendation
        {
            Type = "IdleVirtualMachine",
            ResourceId = resourceId,
            ResourceName = vmName,
            ResourceType = "Microsoft.Compute/virtualMachines",
            ResourceGroup = resourceGroup,
            SubscriptionId = subscriptionId,
            EstimatedMonthlySavings = estimatedMonthlyCost * 0.85m, // 85% economia ao desligar
            Priority = estimatedMonthlyCost > 300 ? "High" : estimatedMonthlyCost > 100 ? "Medium" : "Low",
            Description = $"VM '{vmName}' ({vmSize}) está ligada com CPU média de {metrics.CpuAveragePercent:F1}% nos últimos 7 dias. " +
                         $"Considere desligar ou redimensionar. Economia estimada: R$ {estimatedMonthlyCost * 0.85m:F2}/mês.",
            Tags = ExtractTags(vm)
        };
    }

    /// <summary>
    /// Estima custo mensal da VM baseado no SKU
    /// </summary>
    private decimal EstimateVmMonthlyCost(string vmSize)
    {
        // 💰 Estimativas baseadas em preços Azure Brasil Sul (aproximados)
        return vmSize?.ToLower() switch
        {
            var s when s?.Contains("standard_b1s") == true => 15.00m,
            var s when s?.Contains("standard_b2s") == true => 30.00m,
            var s when s?.Contains("standard_d2s") == true => 80.00m,
            var s when s?.Contains("standard_d4s") == true => 160.00m,
            var s when s?.Contains("standard_d8s") == true => 320.00m,
            var s when s?.Contains("standard_d16s") == true => 640.00m,
            var s when s?.Contains("standard_e4s") == true => 200.00m,
            var s when s?.Contains("standard_e8s") == true => 400.00m,
            var s when s?.Contains("standard_f4s") == true => 150.00m,
            var s when s?.Contains("standard_f8s") == true => 300.00m,
            _ => 100.00m // Valor padrão para SKUs desconhecidos
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
    private Dictionary<string, string> ExtractTags(JsonElement vm)
    {
        var tags = new Dictionary<string, string>();
        
        if (vm.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Object)
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
/// Métricas coletadas da VM
/// </summary>
public class VmMetrics
{
    public double CpuAveragePercent { get; set; }
    public double NetworkInBytes { get; set; }
    public double NetworkOutBytes { get; set; }
}