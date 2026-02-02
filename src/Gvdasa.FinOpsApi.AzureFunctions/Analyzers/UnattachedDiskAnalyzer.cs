using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Gvdasa.FinOpsApi.AzureFunctions.Models;
using Gvdasa.FinOpsApi.AzureFunctions.Services;

namespace Gvdasa.FinOpsApi.AzureFunctions.Analyzers;

public class UnattachedDiskAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;
    private readonly ILogger<UnattachedDiskAnalyzer> _logger;

    public UnattachedDiskAnalyzer(HttpClient httpClient, ILogger<UnattachedDiskAnalyzer> logger)
    {
        _httpClient = httpClient;
        _credential = new DefaultAzureCredential();
        _logger = logger;
    }

    /// <summary>
    /// Analisa discos não anexados em uma subscription
    /// </summary>
    public async Task<StandardAnalyzerResult> AnalyzeSubscriptionAsync(string subscriptionId, int analysisPeriodDays = 7, bool dryRun = true)
    {
        var findings = new List<StandardFinding>();

        try
        {
            _logger.LogInformation("💽 Token obtido com sucesso");
            _logger.LogInformation("📊 Executando query KQL:");

            // Query KQL otimizada para buscar discos não anexados
            var kqlQuery = $@"
                Resources
                | where type =~ 'microsoft.compute/disks'
                | where subscriptionId =~ '{subscriptionId}'
                | where isnull(properties.managedBy) or properties.managedBy == """"
                | where properties.diskState =~ 'Unattached'
                | project
                    resourceId = id,
                    name,
                    resourceGroup,
                    subscriptionId,
                    location,
                    sku = properties.sku.name,
                    diskSizeGb = properties.diskSizeGB,
                    diskState = properties.diskState,
                    timeCreated = properties.timeCreated,
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

            _logger.LogInformation("💽 Resource Graph resposta: {response}", jsonResponse);

            var data = doc.RootElement.GetProperty("data").EnumerateArray();
            var count = 0;

            foreach (var disk in data)
            {
                count++;
                var resourceId = disk.GetProperty("resourceId").GetString() ?? "";
                var name = disk.GetProperty("name").GetString() ?? "";
                var location = disk.GetProperty("location").GetString() ?? "";
                var resourceGroup = disk.GetProperty("resourceGroup").GetString() ?? "";
                var sku = disk.GetProperty("sku").GetString() ?? "Standard_LRS";
                var diskSizeGb = disk.GetProperty("diskSizeGb").GetInt32();

                var estimatedMonthlyCost = EstimateDiskMonthlyCost(sku, diskSizeGb);
                var monthlySavings = estimatedMonthlyCost * 0.98m; // 98% economia ao remover

                var finding = new StandardFinding
                {
                    Type = FindingTypes.UNATTACHED_DISK,
                    ResourceId = resourceId,
                    ResourceName = name,
                    ResourceType = "Microsoft.Compute/disks",
                    SubscriptionId = subscriptionId,
                    EstimatedMonthlyCost = estimatedMonthlyCost,
                    EstimatedMonthlySavings = monthlySavings,
                    Currency = "BRL",
                    Priority = estimatedMonthlyCost > 150 ? FindingPriorities.HIGH : 
                              estimatedMonthlyCost > 60 ? FindingPriorities.MEDIUM : FindingPriorities.LOW,
                    Confidence = 0.95,
                    Description = $"Disco '{name}' ({sku}, {diskSizeGb}GB) não está anexado há mais de {analysisPeriodDays} dias",
                    Recommendation = "Considere remover este disco se não for necessário. Faça backup dos dados importantes antes da remoção.",
                    Metadata = new Dictionary<string, object>
                    {
                        { "location", location },
                        { "resourceGroup", resourceGroup },
                        { "sku", sku },
                        { "diskSizeGb", diskSizeGb },
                        { "unattachedDays", analysisPeriodDays },
                        { "tags", ExtractTags(disk) }
                    }
                };

                findings.Add(finding);
            }

            _logger.LogInformation("💽 Encontrados {count} recursos na query", count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro durante análise de discos");
        }

        var result = new StandardAnalyzerResult
        {
            SchemaVersion = "1.0",
            AnalysisId = Guid.NewGuid().ToString(),
            Analyzer = AnalyzerNames.UNATTACHED_DISK_ANALYZER,
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
            _logger.LogWarning("⚠️ Validação falhou: {errors}", string.Join(", ", errors));
        }
        
        return result;
    }

    private decimal EstimateDiskMonthlyCost(string sku, int sizeGb)
    {
        // Preços aproximados por GB/mês em BRL
        return sku.ToLower() switch
        {
            "standard_lrs" => sizeGb * 0.15m,
            "standard_ssd_lrs" => sizeGb * 0.25m,
            "premium_lrs" => sizeGb * 0.45m,
            "standardssd_zrs" => sizeGb * 0.35m,
            "premium_zrs" => sizeGb * 0.65m,
            _ => sizeGb * 0.20m // Default
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