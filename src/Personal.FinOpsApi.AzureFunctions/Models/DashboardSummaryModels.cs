using System.Text.Json.Serialization;

namespace Personal.FinOpsApi.AzureFunctions.Models;

public static class DashboardSummaryTypes
{
    public const string GlobalDailySummary = "globalDailySummary";
    public const string TeamDailySummary = "teamDailySummary";
    public const string SubscriptionDailySummary = "subscriptionDailySummary";
    public const string RecommendationSummary = "recommendationSummary";
    public const string AnomalySummary = "anomalySummary";
    public const string DatabaseSummary = "databaseSummary";
}

public class DashboardSummaryDocument
{
    [JsonPropertyName("snapshotRevision")]
    public string? SnapshotRevision { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("scopeKey")]
    public string ScopeKey { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("periodId")]
    public string PeriodId { get; set; } = string.Empty;

    [JsonPropertyName("periodFrom")]
    public string PeriodFrom { get; set; } = string.Empty;

    [JsonPropertyName("periodTo")]
    public string PeriodTo { get; set; } = string.Empty;

    [JsonPropertyName("teamId")]
    public string? TeamId { get; set; }

    [JsonPropertyName("teamName")]
    public string? TeamName { get; set; }

    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; set; }

    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }

    [JsonPropertyName("monthlyProjection")]
    public decimal MonthlyProjection { get; set; }

    // Null on legacy documents: their totals have not been checked against a report.
    [JsonPropertyName("financialSummary")]
    public CostReportFinancialSummary? FinancialSummary { get; set; }

    [JsonPropertyName("activeRecommendations")]
    public int ActiveRecommendations { get; set; }

    [JsonPropertyName("potentialMonthlySavings")]
    public decimal PotentialMonthlySavings { get; set; }

    // Savings come from resource-level cost rows and may use another currency than FinancialSummary.
    [JsonPropertyName("savingsCurrency")]
    public string? SavingsCurrency { get; set; }

    [JsonPropertyName("potentialAnnualSavings")]
    public decimal PotentialAnnualSavings { get; set; }

    [JsonPropertyName("resolvedMonthlySavings")]
    public decimal ResolvedMonthlySavings { get; set; }

    [JsonPropertyName("falsePositiveMonthlySavings")]
    public decimal FalsePositiveMonthlySavings { get; set; }

    [JsonPropertyName("anomalyCount")]
    public int AnomalyCount { get; set; }

    [JsonPropertyName("criticalAnomalyCount")]
    public int CriticalAnomalyCount { get; set; }

    [JsonPropertyName("resourcesWithoutOwner")]
    public int ResourcesWithoutOwner { get; set; }

    [JsonPropertyName("databaseCostTotal")]
    public decimal? DatabaseCostTotal { get; set; }

    [JsonPropertyName("sqlCostTotal")]
    public decimal? SqlCostTotal { get; set; }

    [JsonPropertyName("cosmosCostTotal")]
    public decimal? CosmosCostTotal { get; set; }

    [JsonPropertyName("activeSqlRecommendations")]
    public int ActiveSqlRecommendations { get; set; }

    [JsonPropertyName("activeCosmosRecommendations")]
    public int ActiveCosmosRecommendations { get; set; }

    [JsonPropertyName("lowConfidenceCostCount")]
    public int LowConfidenceCostCount { get; set; }

    [JsonPropertyName("notMatchedCostCount")]
    public int NotMatchedCostCount { get; set; }

    [JsonPropertyName("statusCounts")]
    public Dictionary<string, int>? StatusCounts { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class DashboardSummaryBundle
{
    public DashboardSummaryDocument? GlobalSummary { get; set; }
    public DashboardSummaryDocument? RecommendationSummary { get; set; }
    public DashboardSummaryDocument? AnomalySummary { get; set; }
    public DashboardSummaryDocument? DatabaseSummary { get; set; }
    public List<DashboardSummaryDocument> TeamSummaries { get; set; } = new();
    public List<DashboardSummaryDocument> SubscriptionSummaries { get; set; } = new();
}

/// <summary>
/// Observed billing-cycle totals from one anomaly report, not calendar-month totals.
/// Availability is separate from finality: Available does not mean invoiced/reconciled.
/// </summary>
public sealed class CostReportFinancialSummary
{
    [JsonPropertyName("totalCost")]
    public decimal? TotalCost { get; set; }
    [JsonPropertyName("monthlyProjection")]
    public decimal? MonthlyProjection { get; set; }
    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
    [JsonPropertyName("availability")]
    public string Availability { get; set; } = "Missing";
    [JsonPropertyName("finality")]
    public string Finality { get; set; } = "Provisional";
    [JsonPropertyName("periodKind")]
    public string PeriodKind { get; set; } = "SubscriptionBillingCycles";
    [JsonPropertyName("reportDate")]
    public string? ReportDate { get; set; }
    // Report revision, not the import timestamp or a hash of the underlying export.
    [JsonPropertyName("reportGeneratedAt")]
    public DateTime? ReportGeneratedAt { get; set; }
    [JsonPropertyName("earliestDataAsOf")]
    public string? EarliestDataAsOf { get; set; }
    [JsonPropertyName("latestDataAsOf")]
    public string? LatestDataAsOf { get; set; }
    [JsonPropertyName("expectedSubscriptions")]
    public int ExpectedSubscriptions { get; set; }
    [JsonPropertyName("availableSubscriptions")]
    public int AvailableSubscriptions { get; set; }
    [JsonPropertyName("failedSubscriptions")]
    public int FailedSubscriptions { get; set; }
    [JsonPropertyName("missingSubscriptions")]
    public int MissingSubscriptions { get; set; }
    [JsonPropertyName("dailyRevisions")]
    public Dictionary<string, string> DailyRevisions { get; set; } = new();
    [JsonPropertyName("sources")]
    public List<string> Sources { get; set; } = new();
}
