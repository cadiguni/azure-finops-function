using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public sealed class ResourceCostResolver : IResourceCostResolver
{
    public string NormalizeResourceId(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return string.Empty;
        }

        return resourceId.Trim().TrimEnd('/').ToLowerInvariant();
    }

    public ResourceCostResolution Resolve(
        ResourceCostResolveContext context,
        IReadOnlyCollection<CostDiagnosticRow> rows,
        DateTime periodFromUtc,
        DateTime periodToUtc)
    {
        var normalizedId = NormalizeResourceId(context.ResourceId);
        var normalizedType = (context.ResourceType ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedGroup = (context.ResourceGroup ?? string.Empty).Trim().ToLowerInvariant();
        var targetName = ExtractResourceName(normalizedId);
        var rowViews = rows.Select(ToRowView).ToList();

        var observations = new List<string>();

        var resourceIdMatches = BuildCandidates(
            rowViews.Where(r => r.NormalizedResourceId == normalizedId).ToList(),
            CostMatchSources.ResourceId,
            CostMatchConfidenceLevels.High,
            "Match exato por ResourceId.");

        var nameGroupMatches = BuildCandidates(
            rowViews.Where(r =>
                !string.IsNullOrWhiteSpace(targetName)
                && r.ResourceName == targetName
                && r.ResourceGroupName == normalizedGroup
                && r.ResourceType == normalizedType).ToList(),
            CostMatchSources.ResourceNameAndGroup,
            CostMatchConfidenceLevels.Medium,
            "Match por ResourceName + ResourceGroup + ResourceType.");

        var parentIds = BuildParentResourceCandidates(normalizedId, normalizedType);
        var explicitParent = NormalizeResourceId(context.BillingParentResourceId);
        var serverPrefix = ExtractSqlServerPrefix(normalizedId, normalizedType);
        if (!string.IsNullOrEmpty(explicitParent) && serverPrefix != null
            && explicitParent.StartsWith(serverPrefix + "/elasticpools/", StringComparison.Ordinal))
            parentIds.Add(explicitParent);
        var parentRows = rowViews.Where(r => parentIds.Contains(r.NormalizedResourceId)).ToList();

        var parentMatches = BuildCandidates(
            parentRows,
            CostMatchSources.ParentResource,
            CostMatchConfidenceLevels.Medium,
            "Match por parent resource.");

        var serviceHints = BuildServiceHints(normalizedType);
        var serviceMatches = BuildCandidates(
            rowViews.Where(r =>
                r.ResourceGroupName == normalizedGroup
                && (serviceHints.Any(h => ContainsIgnoreCase(r.ServiceName, h))
                    || serviceHints.Any(h => ContainsIgnoreCase(r.MeterCategory, h))
                    || serviceHints.Any(h => ContainsIgnoreCase(r.MeterSubCategory, h)))).ToList(),
            CostMatchSources.ServiceAndMeter,
            CostMatchConfidenceLevels.Low,
            "Match aproximado por ServiceName/Meter em ResourceGroup.");

        var resourceTypeMatches = BuildCandidates(
            rowViews.Where(r => r.ResourceType == normalizedType).ToList(),
            CostMatchSources.ResourceNameAndGroup,
            CostMatchConfidenceLevels.Low,
            "Match por ResourceType.");

        if (resourceIdMatches.Count == 0)
        {
            observations.Add("Sem match exato por ResourceId.");
        }

        if (parentMatches.Count > 0 && normalizedType.StartsWith("microsoft.documentdb/databaseaccounts/", StringComparison.OrdinalIgnoreCase))
        {
            observations.Add("Recurso Cosmos em nível lógico: custo associado ao parent database account.");
        }

        if (parentMatches.Count > 0 && normalizedType.Contains("microsoft.sql/servers/databases", StringComparison.OrdinalIgnoreCase))
        {
            observations.Add("Recurso SQL Database sem custo direto: custo associado ao parent (server/elastic pool).");
        }

        var best = resourceIdMatches.FirstOrDefault()
            ?? nameGroupMatches.FirstOrDefault()
            ?? parentMatches.OrderByDescending(c => c.TotalCost).FirstOrDefault()
            ; // Approximate service/meter candidates remain diagnostic only.

        if (best == null)
        {
            return new ResourceCostResolution
            {
                DailyCost = 0m,
                MonthlyCost = 0m,
                Currency = "BRL",
                PeriodFromUtc = periodFromUtc,
                PeriodToUtc = periodToUtc,
                CostSource = CostMatchSources.NotMatched,
                CostMatchConfidence = CostMatchConfidenceLevels.Low,
                CostMatchedResourceId = null,
                CostNote = "Não foi possível associar custo para este recurso no período consultado.",
                ResourceIdMatches = resourceIdMatches,
                ResourceNameGroupMatches = nameGroupMatches,
                ResourceTypeMatches = resourceTypeMatches,
                ServiceMeterMatches = serviceMatches,
                BestMatch = null,
                Observations = observations
            };
        }

        var days = best.DaysWithCost <= 0 ? 1 : best.DaysWithCost;
        var dailyCost = Math.Round(best.TotalCost / days, 2, MidpointRounding.AwayFromZero);
        var monthlyCost = Math.Round(dailyCost * 30m, 2, MidpointRounding.AwayFromZero);
        var costNote = best.Confidence == CostMatchConfidenceLevels.High
            ? "Custo associado por ResourceId exato."
            : best.Confidence == CostMatchConfidenceLevels.Medium
                ? "Custo associado por parent/relação de recurso. Valor pode representar custo agregado."
                : "Custo aproximado por serviço/meter. Não tratar como valor exato do recurso.";

        return new ResourceCostResolution
        {
            DailyCost = dailyCost,
            MonthlyCost = monthlyCost,
            Currency = best.Currency,
            PeriodFromUtc = periodFromUtc,
            PeriodToUtc = periodToUtc,
            CostSource = best.Source,
            CostMatchConfidence = best.Confidence,
            CostMatchedResourceId = string.IsNullOrWhiteSpace(best.MatchedResourceId) ? null : best.MatchedResourceId,
            CostNote = costNote,
            ResourceIdMatches = resourceIdMatches,
            ResourceNameGroupMatches = nameGroupMatches,
            ResourceTypeMatches = resourceTypeMatches,
            ServiceMeterMatches = serviceMatches,
            BestMatch = best,
            Observations = observations
        };
    }

    private static List<ResourceCostCandidate> BuildCandidates(
        List<RowView> rows,
        string source,
        string confidence,
        string reason)
    {
        if (rows.Count == 0)
        {
            return new List<ResourceCostCandidate>();
        }

        var grouped = rows
            .GroupBy(r => r.NormalizedResourceId)
            .Select(group =>
            {
                var total = group.Sum(r => r.TotalCost);
                var days = group
                    // Zero/credit days are observed days too. Missing days are not inferred as zero.
                    .Where(r => r.UsageDate.HasValue)
                    .Select(r => r.UsageDate!.Value.Date)
                    .Distinct()
                    .Count();
                var representative = group.First();
                return new ResourceCostCandidate
                {
                    Source = source,
                    Confidence = confidence,
                    MatchedResourceId = representative.RawResourceId,
                    ResourceType = representative.RawResourceType,
                    ResourceGroup = representative.RawResourceGroupName,
                    ServiceName = representative.RawServiceName,
                    MeterCategory = representative.RawMeterCategory,
                    MeterSubCategory = representative.RawMeterSubCategory,
                    TotalCost = total,
                    DaysWithCost = days,
                    Currency = representative.Currency,
                    MatchReason = reason
                };
            })
            .OrderByDescending(c => c.TotalCost)
            .ToList();

        return grouped;
    }

    private HashSet<string> BuildParentResourceCandidates(string normalizedResourceId, string normalizedResourceType)
    {
        var parentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(normalizedResourceId))
        {
            return parentIds;
        }

        var parts = normalizedResourceId.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count >= 2)
        {
            var genericParent = "/" + string.Join('/', parts.Take(parts.Count - 2));
            parentIds.Add(genericParent);
        }

        if (normalizedResourceType.StartsWith("microsoft.documentdb/databaseaccounts/", StringComparison.OrdinalIgnoreCase))
        {
            var idx = parts.FindIndex(p => p.Equals("databaseaccounts", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && idx + 1 < parts.Count)
            {
                parentIds.Add("/" + string.Join('/', parts.Take(idx + 2)));
            }
        }

        if (normalizedResourceType.StartsWith("microsoft.sql/servers/databases", StringComparison.OrdinalIgnoreCase))
        {
            var providersIndex = parts.FindIndex(p => p.Equals("providers", StringComparison.OrdinalIgnoreCase));
            if (providersIndex >= 0 && providersIndex + 3 < parts.Count)
            {
                var serverId = "/" + string.Join('/', parts.Take(providersIndex + 4));
                parentIds.Add(serverId);
            }
        }

        return parentIds;
    }

    private static string? ExtractSqlServerPrefix(string normalizedResourceId, string normalizedResourceType)
    {
        if (!normalizedResourceType.StartsWith("microsoft.sql/servers/databases", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = normalizedResourceId.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var providersIndex = parts.FindIndex(p => p.Equals("providers", StringComparison.OrdinalIgnoreCase));
        if (providersIndex < 0 || providersIndex + 3 >= parts.Count)
        {
            return null;
        }

        return "/" + string.Join('/', parts.Take(providersIndex + 4));
    }

    private static string[] BuildServiceHints(string normalizedResourceType)
    {
        if (normalizedResourceType.StartsWith("microsoft.documentdb", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "cosmos", "documentdb" };
        }

        if (normalizedResourceType.StartsWith("microsoft.sql", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "sql", "azure sql", "sql database" };
        }

        if (normalizedResourceType.StartsWith("microsoft.dbfor", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "database" };
        }

        return new[] { normalizedResourceType };
    }

    private static RowView ToRowView(CostDiagnosticRow row)
    {
        var normalizedResourceId = row.ResourceId.Trim().TrimEnd('/').ToLowerInvariant();
        var normalizedType = (row.ResourceType ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedGroup = (row.ResourceGroupName ?? string.Empty).Trim().ToLowerInvariant();

        return new RowView
        {
            RawResourceId = row.ResourceId,
            RawResourceType = row.ResourceType,
            RawResourceGroupName = row.ResourceGroupName,
            RawServiceName = row.ServiceName,
            RawMeterCategory = row.MeterCategory,
            RawMeterSubCategory = row.MeterSubCategory,
            NormalizedResourceId = normalizedResourceId,
            ResourceName = ExtractResourceName(normalizedResourceId),
            ResourceType = normalizedType,
            ResourceGroupName = normalizedGroup,
            ServiceName = (row.ServiceName ?? string.Empty).ToLowerInvariant(),
            MeterCategory = (row.MeterCategory ?? string.Empty).ToLowerInvariant(),
            MeterSubCategory = (row.MeterSubCategory ?? string.Empty).ToLowerInvariant(),
            TotalCost = row.TotalCost,
            Currency = row.Currency,
            UsageDate = row.UsageDate
        };
    }

    private static string ExtractResourceName(string normalizedResourceId)
    {
        if (string.IsNullOrWhiteSpace(normalizedResourceId))
        {
            return string.Empty;
        }

        var parts = normalizedResourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? string.Empty : parts[^1];
    }

    private static bool ContainsIgnoreCase(string text, string value)
        => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private sealed class RowView
    {
        public string RawResourceId { get; init; } = string.Empty;
        public string RawResourceType { get; init; } = string.Empty;
        public string RawResourceGroupName { get; init; } = string.Empty;
        public string RawServiceName { get; init; } = string.Empty;
        public string RawMeterCategory { get; init; } = string.Empty;
        public string RawMeterSubCategory { get; init; } = string.Empty;
        public string NormalizedResourceId { get; init; } = string.Empty;
        public string ResourceName { get; init; } = string.Empty;
        public string ResourceType { get; init; } = string.Empty;
        public string ResourceGroupName { get; init; } = string.Empty;
        public string ServiceName { get; init; } = string.Empty;
        public string MeterCategory { get; init; } = string.Empty;
        public string MeterSubCategory { get; init; } = string.Empty;
        public decimal TotalCost { get; init; }
        public string Currency { get; init; } = "BRL";
        public DateTime? UsageDate { get; init; }
    }
}
