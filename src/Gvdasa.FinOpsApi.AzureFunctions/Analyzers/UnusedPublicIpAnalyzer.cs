using Azure.Identity;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Gvdasa.FinOpsApi.AzureFunctions.Models;

namespace Gvdasa.FinOpsApi.AzureFunctions.Analyzers;

public class UnusedPublicIpAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<UnusedPublicIpAnalyzer> _logger;

    public UnusedPublicIpAnalyzer(HttpClient httpClient, ILogger<UnusedPublicIpAnalyzer> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<CostRecommendation>> AnalyzeAsync(string subscriptionId)
    {
        _logger.LogInformation("🔍 Iniciando análise de Public IPs ociosos para subscription {sub}", subscriptionId);
        
        var recommendations = new List<CostRecommendation>();

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

            _logger.LogInformation("🌐 Consultando API Azure Resource Manager: {url}", url);

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var totalPublicIps = doc.RootElement.GetProperty("value").GetArrayLength();
            _logger.LogInformation("📊 Encontrados {total} Public IPs na subscription", totalPublicIps);

            foreach (var ip in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var properties = ip.GetProperty("properties");
                var ipName = ip.GetProperty("name").GetString() ?? "unknown";

                // 🔑 REGRA DE OCIOSIDADE: Public IP sem ipConfiguration
                if (!properties.TryGetProperty("ipConfiguration", out var ipConfig) || ipConfig.ValueKind == JsonValueKind.Null)
                {
                    var resourceId = ip.GetProperty("id").GetString() ?? "";
                    var resourceGroup = ExtractResourceGroup(resourceId);
                    
                    // 💰 Custo baseado no SKU
                    var sku = properties.TryGetProperty("publicIPAllocationMethod", out var allocationMethod) 
                        ? allocationMethod.GetString() 
                        : "Static";
                    
                    var monthlyCost = sku == "Static" ? 3.65m : 2.50m; // Standard vs Basic
                    
                    recommendations.Add(new CostRecommendation
                    {
                        Type = "UnusedPublicIp",
                        ResourceId = resourceId,
                        ResourceName = ipName,
                        ResourceType = "Microsoft.Network/publicIPAddresses",
                        ResourceGroup = resourceGroup,
                        SubscriptionId = subscriptionId,
                        EstimatedMonthlySavings = monthlyCost,
                        Priority = "High",
                        Description = $"Public IP '{ipName}' não está associado a nenhum recurso e pode ser removido. Economize R$ {monthlyCost:F2}/mês.",
                        Tags = ExtractTags(ip)
                    });

                    _logger.LogInformation("💡 Public IP ocioso detectado: {name} (R$ {cost}/mês)", ipName, monthlyCost);
                }
            }

            _logger.LogInformation("✅ Análise concluída: {count} Public IPs ociosos encontrados", recommendations.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao analisar Public IPs");
            throw;
        }

        return recommendations;
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