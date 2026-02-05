using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;

namespace Personal.FinOpsApi.AzureFunctions.Analyzers;

/// <summary>
/// Analyzer para detectar VMs ligadas mas ociosas (idle)
/// ✨ V2.0: Agora usa MÉTRICAS REAIS do Azure Monitor
/// Maior impacto financeiro na plataforma FinOps
/// </summary>
public class IdleVmAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;
    private readonly AzureMetricsService _metricsService;
    private readonly ILogger<IdleVmAnalyzer> _logger;

    public IdleVmAnalyzer(
        HttpClient httpClient, 
        AzureMetricsService metricsService,
        ILogger<IdleVmAnalyzer> logger)
    {
        _httpClient = httpClient;
        _credential = new DefaultAzureCredential();
        _metricsService = metricsService;
        _logger = logger;
    }

    /// <summary>
    /// Analisa VMs ociosas na subscription
    /// Combina Resource Graph + Azure Monitor Metrics
    /// </summary>
    public async Task<StandardAnalyzerResult> AnalyzeAsync(string subscriptionId, int analysisPeriodDays = 7, bool dryRun = true)
    {
        var findings = new List<StandardFinding>();

        try
        {
            _logger.LogInformation("💽 Token obtido com sucesso");

            // Query KQL para encontrar VMs em execução
            var kqlQuery = $@"
                Resources
                | where type =~ 'microsoft.compute/virtualmachines'
                | where subscriptionId =~ '{subscriptionId}'
                | where properties.extended.instanceView.powerState.displayStatus =~ 'VM running'
                | project
                    resourceId = id,
                    name,
                    resourceGroup,
                    subscriptionId,
                    location,
                    vmSize = properties.hardwareProfile.vmSize,
                    osType = properties.storageProfile.osDisk.osType,
                    powerState = properties.extended.instanceView.powerState.displayStatus,
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

            var runningVms = doc.RootElement.GetProperty("data").EnumerateArray().ToList();

            _logger.LogInformation("🔍 Encontradas {count} VMs ociosas em execução", runningVms.Count);

            foreach (var vm in runningVms)
            {
                var resourceId = vm.GetProperty("resourceId").GetString() ?? "";
                var name = vm.GetProperty("name").GetString() ?? "";
                var location = vm.GetProperty("location").GetString() ?? "";
                var resourceGroup = vm.GetProperty("resourceGroup").GetString() ?? "";
                var vmSize = vm.GetProperty("vmSize").GetString() ?? "Standard_B1s";
                var osType = vm.GetProperty("osType").GetString() ?? "Unknown";

                // 🚀 Obter métricas REAIS do Azure Monitor
                var vmMetrics = await _metricsService.GetVmMetricsAsync(resourceId, analysisPeriodDays);
                
                var avgCpuUsage = vmMetrics.AvgCpuPercentage;
                var totalNetworkGB = vmMetrics.TotalNetworkInGB + vmMetrics.TotalNetworkOutGB;
                var avgNetworkGBPerDay = totalNetworkGB / analysisPeriodDays;

                // 🎯 Regra: VM é ociosa se CPU < 5% E trafego de rede < 0.1GB/dia
                if (avgCpuUsage < 5.0 && avgNetworkGBPerDay < 0.1)
                {
                    var estimatedMonthlyCost = EstimateVmMonthlyCost(vmSize);
                    var monthlySavings = estimatedMonthlyCost * 0.85m; // 85% economia ao desligar

                    var finding = new StandardFinding
                    {
                        Type = FindingTypes.IDLE_VM,
                        ResourceId = resourceId,
                        ResourceName = name,
                        ResourceType = "Microsoft.Compute/virtualMachines",
                        ResourceGroup = resourceGroup,     // ✅ CORRIGIDO: Campo obrigatório
                        Location = location,               // ✅ CORRIGIDO: Campo obrigatório
                        SubscriptionId = subscriptionId,
                        EstimatedMonthlyCost = estimatedMonthlyCost,
                        EstimatedMonthlySavings = monthlySavings,
                        Currency = "BRL",
                        Priority = estimatedMonthlyCost > 900 ? FindingPriorities.HIGH : 
                                  estimatedMonthlyCost > 300 ? FindingPriorities.MEDIUM : FindingPriorities.LOW,
                        Confidence = 0.8,
                        Description = $"VM '{name}' ({vmSize}) ociosa há {analysisPeriodDays} dias: CPU {avgCpuUsage:F1}%, Rede {avgNetworkGBPerDay:F2}GB/dia",
                        Recommendation = "Considere desligar a VM durante períodos de baixo uso ou redimensionar para um SKU menor.",
                        Tags = ExtractTags(vm),            // ✅ CORRIGIDO: Campo no lugar certo
                        Metadata = new Dictionary<string, object>
                        {
                            ["vmSize"] = vmSize,
                            ["osType"] = osType,
                            ["analysisPeriodDays"] = analysisPeriodDays,
                            ["avgCpuPercentage"] = Math.Round(avgCpuUsage, 2),
                            ["avgNetworkGBPerDay"] = Math.Round(avgNetworkGBPerDay, 3),
                            ["totalNetworkInGB"] = Math.Round(vmMetrics.TotalNetworkInGB, 3),
                            ["totalNetworkOutGB"] = Math.Round(vmMetrics.TotalNetworkOutGB, 3),
                            ["metricsSource"] = "AzureMonitor" // 🚀 Métricas reais!
                        }
                    };

                    findings.Add(finding);
                }
            }

            _logger.LogInformation("✅ Análise VMs concluída: {count} VMs ociosas encontradas", findings.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro durante análise de VMs");
        }

        var result = new StandardAnalyzerResult
        {
            SchemaVersion = "1.0",
            AnalysisId = Guid.NewGuid().ToString(),
            Analyzer = AnalyzerNames.IDLE_VM_ANALYZER,
            SubscriptionId = subscriptionId,
            ExecutedAt = DateTime.UtcNow,
            AnalysisPeriodDays = analysisPeriodDays,
            DryRun = dryRun,
            Findings = findings,
            ExecutionMetadata = new Dictionary<string, object>
            {
                { "totalResourcesAnalyzed", findings.Count },
                { "analyzerVersion", "2.0" },
                { "cpuThreshold", 5.0 },
                { "networkThreshold", 1000 }
            }
        };
        
        var (isValid, errors) = AnalyzerContractValidator.ValidateResult(result);
        if (!isValid)
        {
            _logger.LogWarning("⚠️ Validação falhou: {errors}", string.Join(", ", errors));
        }
        
        return result;
    }

    private decimal EstimateVmMonthlyCost(string vmSize)
    {
        // Preços aproximados por mês em BRL (730 horas)
        return vmSize.ToLower() switch
        {
            "standard_b1s" => 45.00m,
            "standard_b1ms" => 90.00m,
            "standard_b2s" => 180.00m,
            "standard_d2s_v3" => 350.00m,
            "standard_d4s_v3" => 700.00m,
            "standard_d8s_v3" => 1400.00m,
            "standard_e2s_v3" => 400.00m,
            "standard_e4s_v3" => 800.00m,
            "standard_f2s_v2" => 250.00m,
            "standard_f4s_v2" => 500.00m,
            _ => 200.00m // Default
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
