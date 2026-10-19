using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories;

/// <summary>
/// Usado quando o Cosmos não está configurado. Sem rollup persistido a API recai no cálculo
/// sob demanda, que é limitado por dia para não pesar na Function.
/// </summary>
public sealed class NoOpDatabaseCostRollupRepository : IDatabaseCostRollupRepository
{
    public Task<IReadOnlyList<CostDatabaseDailyRollup>> GetRangeAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CostDatabaseDailyRollup>>([]);

    public Task UpsertAsync(CostDatabaseDailyRollup rollup, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
