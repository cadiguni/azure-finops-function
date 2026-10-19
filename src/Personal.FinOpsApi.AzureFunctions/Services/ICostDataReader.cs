using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public interface ICostDataReader
{
    Task<ResourceCostReadResult> ReadResourceCostsAsync(
        string subscriptionId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default);

    Task<DailyCostReadResult> ReadDailyCostsAsync(
        string subscriptionId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default);
}

public sealed class ResourceCostReadResult
{
    public CostReadProvenance Provenance { get; init; } = new();
    public IReadOnlyList<CostDiagnosticRow> Rows { get; init; } = [];
}

public sealed class DailyCostReadResult
{
    public string Source { get; init; } = CostDataSources.CostManagementQueryApi;
    public DateTime? DataAsOf { get; init; }
    public bool UsedFallback { get; init; }
    public string? FallbackReason { get; init; }
    public IReadOnlyList<DailyCostEntry> Entries { get; init; } = [];
}
