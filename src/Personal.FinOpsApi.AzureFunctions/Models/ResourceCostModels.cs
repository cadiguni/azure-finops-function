namespace Personal.FinOpsApi.AzureFunctions.Models;

public static class CostMatchSources
{
    public const string ResourceId = "ResourceId";
    public const string ResourceNameAndGroup = "ResourceNameAndGroup";
    public const string ParentResource = "ParentResource";
    public const string ServiceAndMeter = "ServiceAndMeter";
    public const string NotMatched = "NotMatched";
}

public static class CostMatchConfidenceLevels
{
    public const string High = "High";
    public const string Medium = "Medium";
    public const string Low = "Low";
}

public sealed class CostQueryPeriod
{
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
}

public sealed class CostDimensionInfo
{
    public string Name { get; init; } = string.Empty;
}

public sealed class CostDiagnosticRow
{
    public string ResourceId { get; init; } = string.Empty;
    public string ResourceType { get; init; } = string.Empty;
    public string ResourceGroupName { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string MeterCategory { get; init; } = string.Empty;
    public string MeterSubCategory { get; init; } = string.Empty;
    public string Currency { get; init; } = "BRL";
    public decimal TotalCost { get; init; }
    public DateTime? UsageDate { get; init; }
}

public sealed class ResourceCostResolveContext
{
    public string? BillingParentResourceId { get; set; }
    public string ResourceId { get; init; } = string.Empty;
    public string ResourceType { get; init; } = string.Empty;
    public string ResourceGroup { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
}

public sealed class ResourceCostCandidate
{
    public string Source { get; init; } = CostMatchSources.NotMatched;
    public string Confidence { get; init; } = CostMatchConfidenceLevels.Low;
    public string MatchedResourceId { get; init; } = string.Empty;
    public string ResourceType { get; init; } = string.Empty;
    public string ResourceGroup { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string MeterCategory { get; init; } = string.Empty;
    public string MeterSubCategory { get; init; } = string.Empty;
    public decimal TotalCost { get; init; }
    public int DaysWithCost { get; init; }
    public string Currency { get; init; } = "BRL";
    public string MatchReason { get; init; } = string.Empty;
}

public sealed class ResourceCostResolution
{
    public CostReadProvenance? Provenance { get; set; }
    public decimal DailyCost { get; init; }
    public decimal MonthlyCost { get; init; }
    public string Currency { get; init; } = "BRL";
    public DateTime PeriodFromUtc { get; init; }
    public DateTime PeriodToUtc { get; init; }
    public string CostSource { get; init; } = CostMatchSources.NotMatched;
    public string CostMatchConfidence { get; init; } = CostMatchConfidenceLevels.Low;
    public string? CostMatchedResourceId { get; init; }
    public string CostNote { get; init; } = "Não foi possível associar custo para este recurso no período consultado.";
    public IReadOnlyList<ResourceCostCandidate> ResourceIdMatches { get; init; } = Array.Empty<ResourceCostCandidate>();
    public IReadOnlyList<ResourceCostCandidate> ResourceNameGroupMatches { get; init; } = Array.Empty<ResourceCostCandidate>();
    public IReadOnlyList<ResourceCostCandidate> ResourceTypeMatches { get; init; } = Array.Empty<ResourceCostCandidate>();
    public IReadOnlyList<ResourceCostCandidate> ServiceMeterMatches { get; init; } = Array.Empty<ResourceCostCandidate>();
    public ResourceCostCandidate? BestMatch { get; init; }
    public IReadOnlyList<string> Observations { get; init; } = Array.Empty<string>();
}

/// <summary>Read provenance, not proof of continuous coverage or financial reconciliation.</summary>
public sealed class CostReadProvenance
{
    [System.Text.Json.Serialization.JsonPropertyName("datasetRevision")]
    public string? DatasetRevision { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("dailyRevisions")]
    public IReadOnlyDictionary<string, string> DailyRevisions { get; init; } = new Dictionary<string, string>();
    [System.Text.Json.Serialization.JsonPropertyName("costBasis")]
    public string CostBasis { get; init; } = "ActualCost";
    [System.Text.Json.Serialization.JsonPropertyName("source")]
    public string Source { get; init; } = "Unknown";
    [System.Text.Json.Serialization.JsonPropertyName("dataAsOf")]
    public DateTime? DataAsOf { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("importCompletedAt")]
    public DateTimeOffset? ImportCompletedAt { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("exportRunId")]
    public string? ExportRunId { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("publicationMode")]
    public string? PublicationMode { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("usedFallback")]
    public bool UsedFallback { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("fallbackReason")]
    public string? FallbackReason { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("requestedFrom")]
    public DateTime RequestedFrom { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("requestedTo")]
    public DateTime RequestedTo { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("availableDates")]
    public IReadOnlyList<DateTime> AvailableDates { get; init; } = [];
}
