using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories;

public sealed class NoOpDashboardSummaryRepository : IDashboardSummaryRepository
{
    public Task UpsertManyAsync(IEnumerable<DashboardSummaryDocument> documents, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<DashboardSummaryDocument?> GetByTypeAndDateAsync(string type, DateTime date, CancellationToken cancellationToken = default)
        => Task.FromResult<DashboardSummaryDocument?>(null);

    public Task<IReadOnlyList<DashboardSummaryDocument>> ListByTypeAndDateAsync(string type, DateTime date, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DashboardSummaryDocument>>(Array.Empty<DashboardSummaryDocument>());
}
