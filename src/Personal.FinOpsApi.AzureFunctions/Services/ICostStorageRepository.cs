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

    Task<List<CostByResourceRow>> LoadByResourceAsync(
        DateTime dateUtc,
        string subscriptionId,
        CancellationToken cancellationToken = default);

    Task<List<CostByResourceRow>> LoadByResourceAllAsync(
        DateTime dateUtc,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsByResourceDataAsync(DateTime dateUtc, CancellationToken cancellationToken = default);

    Task<bool> CanAccessStorageAsync(CancellationToken cancellationToken = default);
}
