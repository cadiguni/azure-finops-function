namespace Personal.FinOpsApi.AzureFunctions.Services;

public interface IReportSnapshotStore
{
    Task SaveAsync(string revision, IReadOnlyDictionary<string, RecommendationReport> reports, CancellationToken cancellationToken = default);
    Task<RecommendationReport?> LoadAsync(string revision, string scope, CancellationToken cancellationToken = default);
}
