using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Analyzers;

/// <summary>
/// Analyzer para identificar contas Cosmos DB com custo relevante e oportunidade de otimização.
/// </summary>
public class CosmosDatabaseAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;
    private readonly HttpRetryService _httpRetryService;
    private readonly ResourceCostLookupService _costLookupService;
    private readonly ILogger<CosmosDatabaseAnalyzer> _logger;

    public CosmosDatabaseAnalyzer(
        HttpClient httpClient,
        HttpRetryService httpRetryService,
        ResourceCostLookupService costLookupService,
        ILogger<CosmosDatabaseAnalyzer> logger)
    {
        _httpClient = httpClient;
        _credential = new DefaultAzureCredential();
        _httpRetryService = httpRetryService;
        _costLookupService = costLookupService;
        _logger = logger;
    }

    public async Task<StandardAnalyzerResult> AnalyzeAsync(string subscriptionId, int analysisPeriodDays = 30, bool dryRun = true)
    {
        var findings = new List<StandardFinding>();

        try
        {
            await _costLookupService.PreloadCostsAsync(subscriptionId);

            var query = $@"
                Resources
                | where type =~ 'microsoft.documentdb/databaseaccounts'
                | where subscriptionId =~ '{subscriptionId}'
                | project
                    resourceId = id,
                    name,
                    resourceGroup,
                    subscriptionId,
                    location,
                    kind = tostring(kind),
                    offerType = tostring(properties.databaseAccountOfferType),
                    enableFreeTier = tostring(properties.enableFreeTier),
                    publicNetworkAccess = tostring(properties.publicNetworkAccess),
                    tags";

            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }));

            var payload = JsonSerializer.Serialize(new { query });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");

            var response = await _httpRetryService.PostWithRetryAsync(
                _httpClient,
                "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01",
                content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CosmosDatabaseAnalyzer retornou status {statusCode}", (int)response.StatusCode);
                return BuildResult(subscriptionId, analysisPeriodDays, dryRun, findings);
            }

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var accounts = doc.RootElement.GetProperty("data").EnumerateArray().ToList();

            foreach (var account in accounts)
            {
                var resourceId = account.GetProperty("resourceId").GetString() ?? string.Empty;
                var cost = await _costLookupService.GetResourceCostDataAsync(subscriptionId, resourceId);
                findings.Add(CreateFinding(subscriptionId, account, cost));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro durante análise de Cosmos DB para {subscriptionId}", subscriptionId);
        }

        return BuildResult(subscriptionId, analysisPeriodDays, dryRun, findings);
    }

    internal static StandardFinding CreateFinding(string subscriptionId, JsonElement account, ResourceCostData cost)
    {
        var resourceId = account.GetProperty("resourceId").GetString() ?? string.Empty;
        var resourceName = account.GetProperty("name").GetString() ?? string.Empty;
        var resourceGroup = account.GetProperty("resourceGroup").GetString() ?? string.Empty;
        var location = account.GetProperty("location").GetString() ?? string.Empty;
        var kind = account.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() ?? string.Empty : string.Empty;
        var offerType = account.TryGetProperty("offerType", out var offerTypeElement) ? offerTypeElement.GetString() ?? string.Empty : string.Empty;
        var enableFreeTier = account.TryGetProperty("enableFreeTier", out var freeTierElement) ? freeTierElement.GetString() ?? string.Empty : string.Empty;
        var publicNetworkAccess = account.TryGetProperty("publicNetworkAccess", out var networkElement) ? networkElement.GetString() ?? string.Empty : string.Empty;

        var hasCost = !string.Equals(cost.CostSource, CostMatchSources.NotMatched, StringComparison.OrdinalIgnoreCase);
        var estimatedMonthlyCost = hasCost ? cost.MonthlyCost : 0m;

        var dailyCost = hasCost ? cost.DailyCost : 0m;
        // Cost alone is not evidence of underutilization or quantifiable savings.

        var finding = new StandardFinding
        {
            Type = FindingTypes.COSMOS_DB_COST_REVIEW,
            ResourceId = resourceId,
            ResourceName = resourceName,
            ResourceType = "Microsoft.DocumentDB/databaseAccounts",
            ResourceGroup = resourceGroup,
            SubscriptionId = subscriptionId,
            Location = location,
            DailyCost = dailyCost,
            EstimatedMonthlyCost = estimatedMonthlyCost,
            CostProvenance = cost.Provenance,
            ObservedCost = cost.ObservedCost,
            EstimatedMonthlySavings = 0m,
            Currency = cost.Currency,
            Priority = estimatedMonthlyCost >= 900m ? FindingPriorities.HIGH : estimatedMonthlyCost >= 300m ? FindingPriorities.MEDIUM : FindingPriorities.LOW,
            Confidence = 0.3,
            Description = hasCost
                ? $"Investigação sugerida para Cosmos DB '{resourceName}': projeção mensal associada de {estimatedMonthlyCost:F2} {cost.Currency}; utilização ainda não avaliada."
                : $"Investigação sugerida para Cosmos DB '{resourceName}': custo indisponível e utilização ainda não avaliada.",
            Recommendation = "Identificar o modelo de cobrança e coletar consumo de RU, armazenamento, latência e throttling. Em serverless, investigar consumo; em provisionado/autoscale, avaliar capacidade e picos. Economia ainda não estimada.",
            Tags = ExtractTags(account),
            Metadata = new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["offerType"] = offerType,
                ["enableFreeTier"] = enableFreeTier,
                ["publicNetworkAccess"] = publicNetworkAccess,
                ["evidenceStatus"] = "InsufficientMetrics",
                ["savingsStatus"] = "NotEstimated",
                ["costAvailability"] = hasCost ? "Available" : "Missing",
                ["costSource"] = cost.CostSource,
                ["costMatchConfidence"] = cost.CostMatchConfidence,
                ["costMatchedResourceId"] = cost.CostMatchedResourceId ?? resourceId,
                ["costPeriodFrom"] = cost.CostPeriodFrom ?? DateTime.UtcNow.Date.AddDays(-30),
                ["costPeriodTo"] = cost.CostPeriodTo ?? DateTime.UtcNow.Date,
                ["costNote"] = cost.CostNote ?? string.Empty,
                ["resourceGroup"] = resourceGroup,
                ["location"] = location,
                ["tags"] = ExtractTags(account)
            }
        };

        return finding;
    }

    private StandardAnalyzerResult BuildResult(string subscriptionId, int analysisPeriodDays, bool dryRun, List<StandardFinding> findings)
    {
        var result = new StandardAnalyzerResult
        {
            SchemaVersion = "1.0",
            AnalysisId = Guid.NewGuid().ToString(),
            Analyzer = AnalyzerNames.COSMOS_DATABASE_ANALYZER,
            Scope = "subscription",
            SubscriptionId = subscriptionId,
            ExecutedAt = DateTime.UtcNow,
            AnalysisPeriodDays = analysisPeriodDays,
            DryRun = dryRun,
            Findings = findings,
            ExecutionMetadata = new Dictionary<string, object>
            {
                ["totalResourcesAnalyzed"] = findings.Count,
                ["analyzerVersion"] = "1.0"
            }
        };

        var (isValid, errors) = AnalyzerContractValidator.ValidateResult(result);
        if (!isValid)
        {
            _logger.LogWarning("Validação falhou no CosmosDatabaseAnalyzer: {errors}", string.Join(", ", errors));
        }

        return result;
    }

    private static Dictionary<string, string> ExtractTags(JsonElement resource)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (resource.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in tagsElement.EnumerateObject())
            {
                tags[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return tags;
    }
}
