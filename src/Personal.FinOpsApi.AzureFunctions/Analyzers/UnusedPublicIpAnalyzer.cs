using Azure.Identity;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;

namespace Personal.FinOpsApi.AzureFunctions.Analyzers;

public class UnusedPublicIpAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly ResourceCostLookupService _costLookupService;
    private readonly ILogger<UnusedPublicIpAnalyzer> _logger;

    public UnusedPublicIpAnalyzer(HttpClient httpClient, ILogger<UnusedPublicIpAnalyzer> logger, ResourceCostLookupService costLookupService)
    {
        _httpClient = httpClient;
        _costLookupService = costLookupService;
        _logger = logger;
    }

    public async Task<StandardAnalyzerResult> AnalyzeAsync(string subscriptionId, int analysisPeriodDays = 7, bool dryRun = true)
    {
        _logger.LogInformation(" Iniciando análise de Public IPs ociosos para subscription {sub}", subscriptionId);
        
        var findings = new List<StandardFinding>();

        try
        {
            var credential = new DefaultAzureCredential();
            var token = await credential.GetTokenAsync(
                new Azure.Core.TokenRequestContext(
                    new[] { "https://management.azure.com/.default" }
                )
            );

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token.Token);

            var url =
                $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Network/publicIPAddresses?api-version=2023-05-01";

            _logger.LogInformation(" Consultando API Azure Resource Manager: {url}", url);

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var totalPublicIps = doc.RootElement.GetProperty("value").GetArrayLength();
            _logger.LogInformation(" Encontrados {total} Public IPs na subscription", totalPublicIps);

            foreach (var ip in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var properties = ip.GetProperty("properties");
                var ipName = ip.GetProperty("name").GetString() ?? "unknown";

                //  REGRA DE OCIOSIDADE: Public IP sem ipConfiguration
                if (!properties.TryGetProperty("ipConfiguration", out var ipConfig) || ipConfig.ValueKind == JsonValueKind.Null)
                {
                    var resourceId = ip.GetProperty("id").GetString() ?? "";
                    var resourceGroup = ExtractResourceGroup(resourceId);
                    var location = ip.TryGetProperty("location", out var loc) ? loc.GetString() ?? "unknown" : "unknown";
                    
                    //  Custo baseado no SKU
                    var sku = properties.TryGetProperty("publicIPAllocationMethod", out var allocationMethod) 
                        ? allocationMethod.GetString() 
                        : "Static";
                    
                    var cost = await _costLookupService.GetResourceCostDataAsync(subscriptionId, resourceId);
                    var monthlyCost = cost.MonthlyCost;
                    var monthlySavings = 0m; // Investigation alone does not substantiate realizable savings.
                    
                    var finding = new StandardFinding
                    {
                        CostProvenance = cost.Provenance,
                        ObservedCost = cost.ObservedCost,
                        DailyCost = cost.DailyCost,
                        Type = FindingTypes.UNUSED_PUBLIC_IP,
                        ResourceId = resourceId,
                        ResourceName = ipName,
                        ResourceType = "Microsoft.Network/publicIPAddresses",
                        ResourceGroup = resourceGroup,     //  CORRIGIDO: Campo obrigatório
                        Location = location,               //  CORRIGIDO: Campo obrigatório
                        SubscriptionId = subscriptionId,
                        EstimatedMonthlyCost = monthlyCost,
                        EstimatedMonthlySavings = monthlySavings,
                        Currency = cost.Currency,
                        Priority = FindingPriorities.HIGH,
                        Confidence = 0.95,
                        Description = $"Public IP '{ipName}' não possui associação na consulta atual",
                        Recommendation = "Investigar se este IP público ainda é necessário. Verificar se há dependências externas (DNS, firewalls, integrações) antes de qualquer ação.",
                        Tags = ExtractTags(ip),            //  CORRIGIDO: Campo no lugar certo
                        Metadata = new Dictionary<string, object>
                        {
                            { "costSource", cost.CostSource },
                            { "costMatchConfidence", cost.CostMatchConfidence },
                            { "costMatchedResourceId", cost.CostMatchedResourceId ?? resourceId },
                            { "costPeriodFrom", cost.CostPeriodFrom ?? DateTime.UtcNow.Date.AddDays(-30) },
                            { "costPeriodTo", cost.CostPeriodTo ?? DateTime.UtcNow.Date },
                            { "costNote", cost.CostNote ?? "" },
                            { "allocationMethod", sku ?? "Static" },
                            { "associationObservedAt", DateTime.UtcNow }
                        }
                    };
                    
                    findings.Add(finding);
                    _logger.LogInformation(" Public IP ocioso detectado: {name} (R$ {cost}/mês)", ipName, monthlyCost);
                }
            }

            _logger.LogInformation(" Análise concluída: {count} Public IPs ociosos encontrados", findings.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao analisar Public IPs");
        }

        var result = new StandardAnalyzerResult
        {
            SchemaVersion = "1.0",
            AnalysisId = Guid.NewGuid().ToString(),
            Analyzer = AnalyzerNames.UNUSED_PUBLIC_IP_ANALYZER,
            SubscriptionId = subscriptionId,
            ExecutedAt = DateTime.UtcNow,
            AnalysisPeriodDays = analysisPeriodDays,
            DryRun = dryRun,
            Findings = findings,
            ExecutionMetadata = new Dictionary<string, object>
            {
                { "totalResourcesAnalyzed", findings.Count },
                { "analyzerVersion", "2.0" }
            }
        };
        
        var (isValid, errors) = AnalyzerContractValidator.ValidateResult(result);
        if (!isValid)
        {
            _logger.LogWarning(" Validação falhou: {errors}", string.Join(", ", errors));
        }
        
        return result;
    }

    private string ExtractResourceGroup(string resourceId)
    {
        if (string.IsNullOrEmpty(resourceId)) return "unknown";
        
        var parts = resourceId.Split('/');
        var rgIndex = Array.IndexOf(parts, "resourceGroups");
        return rgIndex > -1 && rgIndex + 1 < parts.Length ? parts[rgIndex + 1] : "unknown";
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
