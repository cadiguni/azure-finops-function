using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories;

public interface IOwnershipRepository
{
    Task<IReadOnlyList<OwnershipDocument>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<OwnershipDocument?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertAsync(OwnershipDocument document, CancellationToken cancellationToken = default);
}
