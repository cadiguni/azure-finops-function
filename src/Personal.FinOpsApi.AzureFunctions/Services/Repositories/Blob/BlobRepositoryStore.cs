using Azure.Storage.Blobs;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories.Blob;

internal class BlobRepositoryStore
{
    private readonly BlobContainerClient _container;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public BlobRepositoryStore(BlobServiceClient blobServiceClient, string containerName)
    {
        _container = blobServiceClient.GetBlobContainerClient(containerName);
    }

    public async Task<List<T>> LoadListAsync<T>(string blobPath, CancellationToken cancellationToken = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var blobClient = _container.GetBlobClient(blobPath);
        if (!await blobClient.ExistsAsync(cancellationToken))
        {
            return new List<T>();
        }

        var content = await blobClient.DownloadContentAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<T>>(content.Value.Content.ToString(), _jsonOptions) ?? new List<T>();
    }

    public async Task SaveListAsync<T>(string blobPath, List<T> items, CancellationToken cancellationToken = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var json = JsonSerializer.Serialize(items, _jsonOptions);
        var blobClient = _container.GetBlobClient(blobPath);
        await blobClient.UploadAsync(BinaryData.FromString(json), overwrite: true, cancellationToken);
    }
}
