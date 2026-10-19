using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories;

public interface IDatabaseCostRollupRepository
{
    Task<IReadOnlyList<CostDatabaseDailyRollup>> GetRangeAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(CostDatabaseDailyRollup rollup, CancellationToken cancellationToken = default);
}
