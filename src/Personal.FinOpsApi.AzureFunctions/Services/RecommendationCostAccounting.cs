using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>Amounts attributed to a pool/account are evidence, never a per-child allocation.</summary>
public static class RecommendationCostAccounting
{
    // Name + group + type identifies the same resource (Export ids may differ in format/casing).
    public static string Scope(CostRecommendation r) => !r.ObservedCost.HasValue ? "Missing"
        : r.CostSource == CostMatchSources.ParentResource ? "Shared"
        : r.CostSource is CostMatchSources.ResourceId or CostMatchSources.ResourceNameAndGroup ? "Direct" : "Approximate";

    public static string Key(CostRecommendation r) =>
        (string.IsNullOrWhiteSpace(r.CostMatchedResourceId) ? r.ResourceId : r.CostMatchedResourceId).Trim().TrimEnd('/').ToLowerInvariant();

    public static decimal? ObservedTotal(IEnumerable<CostRecommendation> recommendations, string? currency = null)
    {
        var rows = recommendations
            .Where(recommendation => Scope(recommendation) is "Direct" or "Shared")
            .ToList();
        if (!string.IsNullOrWhiteSpace(currency))
        {
            rows = rows.Where(row => row.CostCurrency.Equals(currency, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (rows.Count == 0) return null;
        if (rows.Select(r => r.CostCurrency.ToUpperInvariant()).Distinct().Count() != 1) return null;
        // Analysis runs in queued steps, so one subscription may legitimately carry several Export
        // revisions/periods. Each charged resource is still counted once.
        return rows.GroupBy(Key).Sum(g => g.Max(r => r.ObservedCost!.Value));
    }

    /// <summary>
    /// Currency in which savings can be totalled: the preferred one when it has quantified savings,
    /// otherwise the currency with the most quantified recommendations. Null when nothing is quantified.
    /// </summary>
    public static string? SavingsCurrency(IEnumerable<CostRecommendation> recommendations, string? preferred = null)
    {
        var currencies = recommendations.Where(IsQuantifiable)
            .Select(r => r.CostCurrency.Trim().ToUpperInvariant())
            .Where(c => c.Length > 0)
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key).ToList();
        if (!string.IsNullOrWhiteSpace(preferred) && currencies.Contains(preferred.Trim().ToUpperInvariant()))
            return preferred.Trim().ToUpperInvariant();
        return currencies.FirstOrDefault();
    }

    private static bool IsQuantifiable(CostRecommendation r) =>
        Scope(r) == "Direct" && r.ObservedCost > 0 && r.EstimatedMonthlySavings > 0;

    // Overlapping actions on one charged resource are alternatives, not additive savings.
    // Shared/approximate costs cannot substantiate savings for an individual child.
    public static decimal Savings(IEnumerable<CostRecommendation> recommendations, string? currency = null)
    {
        var rows = recommendations.Where(IsQuantifiable).ToList();
        if (!string.IsNullOrWhiteSpace(currency))
        {
            rows = rows.Where(row => row.CostCurrency.Equals(currency, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (rows.Select(r => r.CostCurrency.ToUpperInvariant()).Distinct().Count() > 1) return 0;
        return rows.GroupBy(Key).Sum(g => g.Max(r => Math.Min(r.EstimatedMonthlySavings, Math.Max(0, r.EstimatedMonthlyCost))));
    }
}
