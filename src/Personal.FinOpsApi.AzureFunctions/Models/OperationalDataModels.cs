namespace Personal.FinOpsApi.AzureFunctions.Models;

public static class RecommendationStatuses
{
    public const string Active = "Active";
    public const string New = "New";
    public const string InReview = "InReview";
    public const string Accepted = "Accepted";
    public const string FalsePositive = "FalsePositive";
    public const string Ignored = "Ignored";
    public const string Snoozed = "Snoozed";
    public const string ApprovedForCleanup = "ApprovedForCleanup";
    public const string Resolved = "Resolved";

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        Active,
        New,
        InReview,
        Accepted,
        FalsePositive,
        Ignored,
        Snoozed,
        ApprovedForCleanup,
        Resolved
    };

    public static bool IsValid(string? status) => !string.IsNullOrWhiteSpace(status) && Allowed.Contains(status);
}

public class OwnershipDocument
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "ownership";
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string? ResourceGroup { get; set; }
    public string? ResourceId { get; set; }
    public string TeamId { get; set; } = string.Empty;
    public string Source { get; set; } = "manual";
    public decimal Confidence { get; set; } = 1.0m;
    public bool ManualOverride { get; set; } = true;
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class RecommendationStatusDocument
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "recommendationStatus";
    public string ResourceId { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string? TeamId { get; set; }
    public string RecommendationType { get; set; } = string.Empty;
    public string Status { get; set; } = RecommendationStatuses.New;
    public string? Reason { get; set; }
    public DateTime? SnoozeUntil { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AuditLogDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Type { get; set; } = "auditLog";
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string PerformedBy { get; set; } = string.Empty;
    public DateTime PerformedAt { get; set; } = DateTime.UtcNow;
    public object? Before { get; set; }
    public object? After { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
