using System.Text.Json.Serialization;

namespace Personal.FinOpsApi.AzureFunctions.Models;

/// <summary>Evidence about published exports, not an assertion that collectors executed successfully.</summary>
public sealed record DataHealthResponse(
    [property: JsonPropertyName("subscriptionId")] string SubscriptionId,
    [property: JsonPropertyName("publicationMode")] string PublicationMode,
    [property: JsonPropertyName("from")] DateOnly From,
    [property: JsonPropertyName("to")] DateOnly To,
    [property: JsonPropertyName("checkedAt")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("lastImportedAt")] DateTimeOffset? LastImportedAt,
    [property: JsonPropertyName("latestUsageDate")] DateOnly? LatestUsageDate,
    [property: JsonPropertyName("isStale")] bool? IsStale,
    [property: JsonPropertyName("coveragePercent")] decimal? CoveragePercent,
    [property: JsonPropertyName("availableDates")] IReadOnlyList<string>? AvailableDates,
    [property: JsonPropertyName("missingDates")] IReadOnlyList<string>? MissingDates,
    [property: JsonPropertyName("lastPublicationStatus")] string? LastPublicationStatus,
    [property: JsonPropertyName("historicalFailureCount")] int? HistoricalFailureCount,
    [property: JsonPropertyName("lastCollectorRunAt")] DateTimeOffset? LastCollectorRunAt,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings)
{
    [JsonPropertyName("queueStatus")] public string QueueStatus => "unavailable";
    [JsonPropertyName("timerStatus")] public string TimerStatus => "unavailable";
}

/// <summary>One row of the admin data health overview (same evidence as <see cref="DataHealthResponse"/>).</summary>
public sealed record DataHealthOverviewRow(
    [property: JsonPropertyName("subscriptionId")] string SubscriptionId,
    [property: JsonPropertyName("subscriptionName")] string? SubscriptionName,
    [property: JsonPropertyName("exportIngestion")] bool ExportIngestion,
    [property: JsonPropertyName("publicationMode")] string PublicationMode,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("coveragePercent")] decimal? CoveragePercent,
    [property: JsonPropertyName("missingDays")] int? MissingDays,
    [property: JsonPropertyName("latestUsageDate")] DateOnly? LatestUsageDate,
    [property: JsonPropertyName("isStale")] bool? IsStale,
    [property: JsonPropertyName("lastImportedAt")] DateTimeOffset? LastImportedAt,
    [property: JsonPropertyName("lastPublicationStatus")] string? LastPublicationStatus);

public sealed record DataHealthOverviewResponse(
    [property: JsonPropertyName("from")] DateOnly From,
    [property: JsonPropertyName("to")] DateOnly To,
    [property: JsonPropertyName("checkedAt")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("subscriptionSource")] string SubscriptionSource,
    [property: JsonPropertyName("subscriptions")] IReadOnlyList<DataHealthOverviewRow> Subscriptions,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

/// <summary>Monthly calendar budget; a cost center is scoped to one subscription and imported tags only.</summary>
public sealed record GovernanceBudgetInput(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("subscriptionId")] string SubscriptionId,
    [property: JsonPropertyName("month")] string Month,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("costCenter")] string? CostCenter = null,
    [property: JsonPropertyName("version")] string? Version = null);

/// <summary>Persisted budget and optimistic concurrency token. Membership is not inferred from ownership.</summary>
public sealed record GovernanceBudget(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("definition")] GovernanceBudgetInput Definition,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("updatedBy")] string UpdatedBy,
    [property: JsonPropertyName("version")] string Version);

/// <summary>Actual observed consumption and explicitly estimated linear projection.</summary>
public sealed record GovernanceBudgetEvaluation(
    [property: JsonPropertyName("budget")] GovernanceBudget Budget,
    [property: JsonPropertyName("publicationMode")] string PublicationMode,
    [property: JsonPropertyName("observedCost")] decimal? ObservedCost,
    [property: JsonPropertyName("consumptionPercent")] decimal? ConsumptionPercent,
    [property: JsonPropertyName("estimatedMonthEnd")] decimal? EstimatedMonthEnd,
    [property: JsonPropertyName("forecastReliable")] bool ForecastReliable,
    [property: JsonPropertyName("forecastExceedsBudget")] bool? ForecastExceedsBudget,
    [property: JsonPropertyName("reachedThresholds")] IReadOnlyList<int> ReachedThresholds,
    [property: JsonPropertyName("expectedDays")] int ExpectedDays,
    [property: JsonPropertyName("observedDays")] int ObservedDays,
    [property: JsonPropertyName("missingDates")] IReadOnlyList<string> MissingDates,
    [property: JsonPropertyName("dataAsOf")] DateOnly? DataAsOf,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings)
{
    [JsonPropertyName("notificationDelivery")] public string NotificationDelivery => "notConfigured";
}