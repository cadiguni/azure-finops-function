using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories;

public interface IDashboardSummaryRepository
{
    Task UpsertManyAsync(IEnumerable<DashboardSummaryDocument> documents, CancellationToken cancellationToken = default);

    Task<DashboardSummaryDocument?> GetByTypeAndDateAsync(
        string type,
        DateTime date,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardSummaryDocument>> ListByTypeAndDateAsync(
        string type,
        DateTime date,
        CancellationToken cancellationToken = default);
}
