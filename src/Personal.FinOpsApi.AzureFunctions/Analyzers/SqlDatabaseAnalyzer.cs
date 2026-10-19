using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Analyzers;

/// <summary>
/// Analyzer para identificar SQL Databases com custo relevante e possível oportunidade de rightsizing.
/// </summary>
public class SqlDatabaseAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;
    private readonly HttpRetryService _httpRetryService;
    private readonly ResourceCostLookupService _costLookupService;
    private readonly ILogger<SqlDatabaseAnalyzer> _logger;

    public SqlDatabaseAnalyzer(
        HttpClient httpClient,
        HttpRetryService httpRetryService,
        ResourceCostLookupService costLookupService,
        ILogger<SqlDatabaseAnalyzer> logger)
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
                | where type =~ 'microsoft.sql/servers/databases'
                | where subscriptionId =~ '{subscriptionId}'
                | where name !~ 'master'
                | project
                    resourceId = id,
                    name,
                    resourceGroup,
                    subscriptionId,
                    location,
                    elasticPoolId = tostring(properties.elasticPoolId),
                    skuName = tostring(sku.name),
                    skuTier = tostring(sku.tier),
                    status = tostring(properties.status),
                    serviceObjective = tostring(properties.currentServiceObjectiveName),
                    maxSizeBytes = tostring(properties.maxSizeBytes),
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
                _logger.LogWarning("SqlDatabaseAnalyzer retornou status {statusCode}", (int)response.StatusCode);
                return BuildResult(subscriptionId, analysisPeriodDays, dryRun, findings);
            }

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var databases = doc.RootElement.GetProperty("data").EnumerateArray().ToList();

            foreach (var db in databases)
            {
                var resourceId = db.GetProperty("resourceId").GetString() ?? string.Empty;
                var cost = await _costLookupService.GetResourceCostDataAsync(subscriptionId, resourceId,
                    db.TryGetProperty("elasticPoolId", out var pool) ? pool.GetString() : null);
                findings.Add(CreateFinding(subscriptionId, db, cost));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro durante análise de SQL Databases para {subscriptionId}", subscriptionId);
        }

        return BuildResult(subscriptionId, analysisPeriodDays, dryRun, findings);
    }

    internal static StandardFinding CreateFinding(string subscriptionId, JsonElement db, ResourceCostData cost)
    {
        var resourceId = db.GetProperty("resourceId").GetString() ?? string.Empty;
        var resourceName = db.GetProperty("name").GetString() ?? string.Empty;
        var resourceGroup = db.GetProperty("resourceGroup").GetString() ?? string.Empty;
        var location = db.GetProperty("location").GetString() ?? string.Empty;
        var skuName = db.TryGetProperty("skuName", out var skuNameElement) ? skuNameElement.GetString() ?? string.Empty : string.Empty;
        var skuTier = db.TryGetProperty("skuTier", out var skuTierElement) ? skuTierElement.GetString() ?? string.Empty : string.Empty;
        var serviceObjective = db.TryGetProperty("serviceObjective", out var objectiveElement) ? objectiveElement.GetString() ?? string.Empty : string.Empty;
        var status = db.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? string.Empty : string.Empty;

        var hasCost = !string.Equals(cost.CostSource, CostMatchSources.NotMatched, StringComparison.OrdinalIgnoreCase);
        var estimatedMonthlyCost = hasCost ? cost.MonthlyCost : 0m;

        var dailyCost = hasCost ? cost.DailyCost : 0m;
        // Cost alone is not evidence of underutilization or quantifiable savings.

        var finding = new StandardFinding
        {
            Type = FindingTypes.SQL_DATABASE_COST_REVIEW,
            ResourceId = resourceId,
            ResourceName = resourceName,
            ResourceType = "Microsoft.Sql/servers/databases",
            ResourceGroup = resourceGroup,
            SubscriptionId = subscriptionId,
            Location = location,
            DailyCost = dailyCost,
            EstimatedMonthlyCost = estimatedMonthlyCost,
            CostProvenance = cost.Provenance,
            ObservedCost = cost.ObservedCost,
            EstimatedMonthlySavings = 0m,
            Currency = cost.Currency,
            Priority = estimatedMonthlyCost >= 600m ? FindingPriorities.HIGH : estimatedMonthlyCost >= 200m ? FindingPriorities.MEDIUM : FindingPriorities.LOW,
            Confidence = 0.3,
            Description = hasCost
                ? $"Investigação sugerida para SQL Database '{resourceName}': projeção mensal associada de {estimatedMonthlyCost:F2} {cost.Currency}; utilização ainda não avaliada."
                : $"Investigação sugerida para SQL Database '{resourceName}': custo indisponível e utilização ainda não avaliada.",
            Recommendation = "Coletar CPU, I/O, armazenamento e picos em período representativo; verificar se o banco pertence a elastic pool. Validar necessidade e impacto antes de propor dimensionamento. Economia ainda não estimada.",
            Tags = ExtractTags(db),
            Metadata = new Dictionary<string, object>
            {
                ["skuName"] = skuName,
                ["skuTier"] = skuTier,
                ["serviceObjective"] = serviceObjective,
                ["status"] = status,
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
                ["tags"] = ExtractTags(db)
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
            Analyzer = AnalyzerNames.SQL_DATABASE_ANALYZER,
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
            _logger.LogWarning("Validação falhou no SqlDatabaseAnalyzer: {errors}", string.Join(", ", errors));
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
