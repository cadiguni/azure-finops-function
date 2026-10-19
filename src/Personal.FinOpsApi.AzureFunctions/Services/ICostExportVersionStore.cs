using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public interface ICostExportVersionStore
{
    Task<CostExportRevisionIndex?> LoadIndexAsync(string subscriptionId, string mode, CancellationToken cancellationToken = default);
    Task<CostExportDailySnapshot> LoadSnapshotAsync(string subscriptionId, string mode, string revision, CancellationToken cancellationToken = default);
    Task<bool> HasPublishedRunAsync(string subscriptionId, string mode, string exportName, string runId, CancellationToken cancellationToken = default);
    Task PublishAsync(CostExportImportCheckpoint checkpoint, IReadOnlyList<CostExportDailySnapshot> snapshots, CancellationToken cancellationToken = default);
}
