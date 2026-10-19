using Azure.Storage.Blobs;
using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories.Blob;

public class BlobOwnershipRepository : IOwnershipRepository
{
    private readonly BlobRepositoryStore _store;
    private readonly string _blobPath;

    public BlobOwnershipRepository(BlobServiceClient blobServiceClient, IConfiguration configuration)
    {
        var containerName = configuration["CONFIG_CONTAINER_NAME"] ?? "finops-config";
        _blobPath = configuration["OwnershipDocumentsBlobPath"] ?? "operational/ownership-documents.json";
        _store = new BlobRepositoryStore(blobServiceClient, containerName);
    }

    public async Task<IReadOnlyList<OwnershipDocument>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _store.LoadListAsync<OwnershipDocument>(_blobPath, cancellationToken);
    }

    public async Task<OwnershipDocument?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var items = await _store.LoadListAsync<OwnershipDocument>(_blobPath, cancellationToken);
        return items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task UpsertAsync(OwnershipDocument document, CancellationToken cancellationToken = default)
    {
        var items = await _store.LoadListAsync<OwnershipDocument>(_blobPath, cancellationToken);
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
