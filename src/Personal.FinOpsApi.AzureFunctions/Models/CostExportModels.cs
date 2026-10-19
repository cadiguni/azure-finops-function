namespace Personal.FinOpsApi.AzureFunctions.Models;

public sealed class CostExportRecord
{
    public string SubscriptionId { get; init; } = string.Empty;
    public DateTime UsageDate { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
    public string ResourceGroup { get; init; } = string.Empty;
    public string ResourceLocation { get; init; } = string.Empty;
    public string ResourceType { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;
    public decimal Cost { get; init; }
    public string Currency { get; init; } = "BRL";
}

public sealed class CostExportImportResult
{
    public int ManifestsFound { get; init; }
    public int ManifestsProcessed { get; init; }
    public int ManifestsSkippedByCheckpoint { get; init; }
    public int SubscriptionsProcessed { get; init; }
    public int PartitionsProcessed { get; init; }
    public long ImportedRows { get; init; }
    public int PublishedDates { get; init; }
    // Only manifests promoted in this invocation appear here.  Consumers can use
    // this list to trigger downstream work without reprocessing checkpoints.
    public IReadOnlyList<CostExportAnalysisTarget> AnalysisTargets { get; init; } = [];
    public IReadOnlyList<string> Failures { get; init; } = [];
}

public sealed record CostExportAnalysisTarget(string SubscriptionId, string RunId);

public sealed class CostExportManifest
{
    public string ManifestVersion { get; init; } = string.Empty;
    public long ByteCount { get; init; }
    public int BlobCount { get; init; }
    public long DataRowCount { get; init; }
    public CostExportManifestExportConfig ExportConfig { get; init; } = new();
    public CostExportManifestDeliveryConfig DeliveryConfig { get; init; } = new();
    public CostExportManifestRunInfo RunInfo { get; init; } = new();
    public IReadOnlyList<CostExportManifestBlob> Blobs { get; init; } = [];
}

public sealed class CostExportManifestExportConfig
{
    public string ExportName { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
    public string DataVersion { get; init; } = string.Empty;
    public string ApiVersion { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string TimeFrame { get; init; } = string.Empty;
    public string Granularity { get; init; } = string.Empty;
}

public sealed class CostExportManifestDeliveryConfig
{
    public bool PartitionData { get; init; }
    public string DataOverwriteBehavior { get; init; } = string.Empty;
    public string FileFormat { get; init; } = string.Empty;
    public string CompressionMode { get; init; } = string.Empty;
    public string RootFolderPath { get; init; } = string.Empty;
}

public sealed class CostExportManifestRunInfo
{
    public string ExecutionType { get; init; } = string.Empty;
    public DateTimeOffset SubmittedTime { get; init; }
    public string RunId { get; init; } = string.Empty;
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
}

public sealed class CostExportManifestBlob
{
    public string BlobName { get; init; } = string.Empty;
    public long ByteCount { get; init; }
    public long DataRowCount { get; init; }
}

public sealed class CostExportImportCheckpoint
{
    public string Source { get; init; } = CostDataSources.CostManagementExport;
    public string SubscriptionId { get; init; } = string.Empty;
    public string ExportName { get; init; } = string.Empty;
    public string RunId { get; init; } = string.Empty;
    // Checkpoints anteriores ao shadow mode publicavam nos paths oficiais.
    public string PublicationMode { get; init; } = CostExportPublicationModes.Cutover;
    public string SourceManifest { get; init; } = string.Empty;
    public DateTimeOffset SubmittedTime { get; init; }
    public DateTime? MinUsageDate { get; init; }
    public DateTime? MaxUsageDate { get; init; }
    public DateTimeOffset ProcessingStartedAt { get; init; }
    public DateTimeOffset ProcessingCompletedAt { get; init; }
    public int PartitionsProcessed { get; init; }
    public long RowsProcessed { get; init; }
    public int PublishedDates { get; init; }
    public IReadOnlyDictionary<string, decimal> TotalsByCurrency { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> PublishedBlobs { get; init; } = [];
    public string Status { get; init; } = "Completed";
}

public sealed class CostExportPublicationWatermark
{
    public string Source { get; init; } = CostDataSources.CostManagementExport;
    public string SubscriptionId { get; init; } = string.Empty;
    public string ExportName { get; init; } = string.Empty;
    public string RunId { get; init; } = string.Empty;
    public string PublicationMode { get; init; } = CostExportPublicationModes.Cutover;
    public DateTimeOffset SubmittedTime { get; init; }
    public DateTime? MinUsageDate { get; init; }
    public DateTime? MaxUsageDate { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }
    public int PartitionsProcessed { get; init; }
    public long RowsProcessed { get; init; }
    public IReadOnlyDictionary<string, decimal> TotalsByCurrency { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> PublishedBlobs { get; init; } = [];
    public string Status { get; init; } = "Completed";
}

public sealed class CostExportImportCursor
{
    public string LastSubscriptionId { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class CostAllocationSummary
{
    [System.Text.Json.Serialization.JsonPropertyName("source")]
    public string Source { get; init; } = CostDataSources.CostManagementExport;

    [System.Text.Json.Serialization.JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("usageDate")]
    public DateTime UsageDate { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("currency")]
    public string Currency { get; init; } = "BRL";

    [System.Text.Json.Serialization.JsonPropertyName("totalCost")]
    public decimal TotalCost { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("allocatedResourceCost")]
    public decimal AllocatedResourceCost { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("unallocatedCost")]
    public decimal UnallocatedCost { get; init; }
}

public static class CostDataSources
{
    public const string CostManagementExport = "CostManagementExport";
    public const string CostManagementQueryApi = "CostManagementQueryApi";
}

public static class CostExportPublicationModes
{
    public const string Shadow = "Shadow";
    public const string Cutover = "Cutover";
}

public sealed class CostExportImportException : Exception
{
    public CostExportImportException(CostExportImportResult result)
        : base($"A importação terminou com {result.Failures.Count} falha(s).")
    {
        Result = result;
    }

    public CostExportImportResult Result { get; }
}
