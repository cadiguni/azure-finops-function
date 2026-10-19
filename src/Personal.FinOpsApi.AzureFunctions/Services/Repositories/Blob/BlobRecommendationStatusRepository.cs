using Azure.Storage.Blobs;
using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories.Blob;

public class BlobRecommendationStatusRepository : IRecommendationStatusRepository
{
    private readonly BlobRepositoryStore _store;
    private readonly string _blobPath;

    public BlobRecommendationStatusRepository(BlobServiceClient blobServiceClient, IConfiguration configuration)
    {
        var containerName = configuration["CONFIG_CONTAINER_NAME"] ?? "finops-config";
        _blobPath = configuration["RecommendationStatusBlobPath"] ?? "operational/recommendation-status.json";
        _store = new BlobRepositoryStore(blobServiceClient, containerName);
    }

    public async Task<IReadOnlyList<RecommendationStatusDocument>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _store.LoadListAsync<RecommendationStatusDocument>(_blobPath, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, RecommendationStatusDocument>> GetByIdsAsync(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        var idSet = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = await _store.LoadListAsync<RecommendationStatusDocument>(_blobPath, cancellationToken);
        return items
            .Where(i => idSet.Contains(i.Id))
            .ToDictionary(i => i.Id, i => i, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<RecommendationStatusDocument?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var items = await _store.LoadListAsync<RecommendationStatusDocument>(_blobPath, cancellationToken);
        return items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task UpsertAsync(RecommendationStatusDocument document, CancellationToken cancellationToken = default)
    {
        var items = await _store.LoadListAsync<RecommendationStatusDocument>(_blobPath, cancellationToken);
        var existing = items.FindIndex(i => i.Id.Equals(document.Id, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            items[existing] = document;
        }
        else
        {
            items.Add(document);
        }

        await _store.SaveListAsync(_blobPath, items, cancellationToken);
    }
}
