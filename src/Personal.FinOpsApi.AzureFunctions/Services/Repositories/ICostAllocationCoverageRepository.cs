using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories;

public interface ICostAllocationCoverageRepository
{
    Task<IReadOnlyList<CostAllocationCoverageDaily>> GetRangeAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(CostAllocationCoverageDaily coverage, CancellationToken cancellationToken = default);
}
