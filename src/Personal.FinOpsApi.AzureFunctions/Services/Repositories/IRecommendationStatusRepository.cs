using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories;

public interface IRecommendationStatusRepository
{
    Task<IReadOnlyList<RecommendationStatusDocument>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, RecommendationStatusDocument>> GetByIdsAsync(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default);

    Task<RecommendationStatusDocument?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertAsync(RecommendationStatusDocument document, CancellationToken cancellationToken = default);
}
