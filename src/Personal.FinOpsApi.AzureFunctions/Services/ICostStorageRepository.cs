using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public interface ICostStorageRepository
{
    Task SaveByServiceAsync(
        DateTime dateUtc,
        string subscriptionId,
        IReadOnlyCollection<CostByServiceRow> rows,
        string? rawJson = null,
        CancellationToken cancellationToken = default);

    Task<List<CostByServiceRow>> LoadByServiceAsync(
        DateTime dateUtc,
        string subscriptionId,
        CancellationToken cancellationToken = default);

    Task<List<CostByServiceRow>> LoadExportByServiceAsync(
        DateTime dateUtc,
        string subscriptionId,
        string publicationMode,
        CancellationToken cancellationToken = default);

    Task<List<CostByServiceRow>> LoadByServiceAllAsync(
        DateTime dateUtc,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsByServiceDataAsync(DateTime dateUtc, CancellationToken cancellationToken = default);

    Task SaveByResourceAsync(
        DateTime dateUtc,
        string subscriptionId,
        IReadOnlyCollection<CostByResourceRow> rows,
        string? rawJson = null,
        CancellationToken cancellationToken = default);

    Task SaveExportByServiceAsync(
        DateTime dateUtc,
        string subscriptionId,
        IReadOnlyCollection<CostByServiceRow> rows,
        string publicationMode,
        CancellationToken cancellationToken = default);

    Task SaveExportByResourceAsync(
        DateTime dateUtc,
        string subscriptionId,
        IReadOnlyCollection<CostByResourceRow> rows,
        string publicationMode,
        CancellationToken cancellationToken = default);

    Task SaveExportAllocationAsync(
        DateTime dateUtc,
        string subscriptionId,
        IReadOnlyCollection<CostAllocationSummary> summaries,
        string publicationMode,
        CancellationToken cancellationToken = default);

    Task<List<CostAllocationSummary>> LoadAllocationAsync(
        DateTime dateUtc,
        string subscriptionId,
        CancellationToken cancellationToken = default);

    Task<List<CostAllocationSummary>> LoadAllocationAllAsync(
        DateTime dateUtc,
        CancellationToken cancellationToken = default);

    Task<List<CostByResourceRow>> LoadByResourceAsync(
        DateTime dateUtc,
        string subscriptionId,
        CancellationToken cancellationToken = default);

    Task<List<CostByResourceRow>> LoadExportByResourceAsync(
        DateTime dateUtc,
        string subscriptionId,
        string publicationMode,
        CancellationToken cancellationToken = default);

    Task<List<CostByResourceRow>> LoadByResourceAllAsync(
        DateTime dateUtc,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsByResourceDataAsync(DateTime dateUtc, CancellationToken cancellationToken = default);

    Task<bool> CanAccessStorageAsync(CancellationToken cancellationToken = default);

    Task<CostExportImportCheckpoint?> LoadImportCheckpointAsync(
        string subscriptionId,
        string exportName,
        string runId,
        string publicationMode,
        CancellationToken cancellationToken = default);

    Task<IAsyncDisposable?> TryAcquireExportPublicationLeaseAsync(
        string subscriptionId,
        string exportName,
        string publicationMode,
        DateTime? maxUsageDate,
        DateTimeOffset submittedTime,
        string runId,
        CancellationToken cancellationToken = default);

    Task<CostExportImportCheckpoint?> LoadLatestCompletedImportCheckpointAsync(
        string subscriptionId,
        string exportName,
        string publicationMode,
        CancellationToken cancellationToken = default);

    Task SaveImportRunCheckpointAsync(
        CostExportImportCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task UpdateLatestImportCheckpointAsync(
        CostExportImportCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task SavePublicationWatermarkAsync(
        CostExportPublicationWatermark watermark,
        CancellationToken cancellationToken = default);

    Task<CostExportPublicationWatermark?> LoadPublicationWatermarkAsync(
        string subscriptionId,
        string publicationMode,
        CancellationToken cancellationToken = default);

    Task<CostExportImportCursor?> LoadImportCursorAsync(
        CancellationToken cancellationToken = default);

    Task SaveImportCursorAsync(
        CostExportImportCursor cursor,
        CancellationToken cancellationToken = default);
}
