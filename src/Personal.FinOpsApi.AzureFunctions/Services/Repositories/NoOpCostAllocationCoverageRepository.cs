using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories;

/// <summary>Usado quando o Cosmos não está configurado: a API recai no cálculo sob demanda.</summary>
public sealed class NoOpCostAllocationCoverageRepository : ICostAllocationCoverageRepository
{
    public Task<IReadOnlyList<CostAllocationCoverageDaily>> GetRangeAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CostAllocationCoverageDaily>>([]);

    public Task UpsertAsync(CostAllocationCoverageDaily coverage, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
