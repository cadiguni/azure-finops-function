using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public class CostStorageRepository : ICostStorageRepository
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<CostStorageRepository> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public CostStorageRepository(
        BlobServiceClient blobServiceClient,
        IConfiguration configuration,
        ILogger<CostStorageRepository> logger)
    {
        var containerName = configuration["COST_STORAGE_CONTAINER"] ?? "finops-analysis";
        _container = blobServiceClient.GetBlobContainerClient(containerName);
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public async Task SaveByServiceAsync(
        DateTime dateUtc,
        string subscriptionId,
        IReadOnlyCollection<CostByServiceRow> rows,
        string? rawJson = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);

        var byServicePath = BuildByServicePath(dateUtc, subscriptionId);
        var byServiceBlob = _container.GetBlobClient(byServicePath);
        await using (var byServiceStream = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rows, _jsonOptions))))
        {
            await byServiceBlob.UploadAsync(byServiceStream, overwrite: true, cancellationToken: cancellationToken);
        }

        _logger.LogInformation(
            "Cost by service salvo para {subscriptionId} em {date}: {path} ({count} linhas)",
            subscriptionId,
            dateUtc.ToString("yyyy-MM-dd"),
            byServicePath,
            rows.Count);

        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            var rawPath = BuildRawPath(dateUtc, subscriptionId);
            var rawBlob = _container.GetBlobClient(rawPath);
            await using var rawStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawJson));
            await rawBlob.UploadAsync(rawStream, overwrite: true, cancellationToken: cancellationToken);
        }
    }

    public async Task<List<CostByServiceRow>> LoadByServiceAsync(
        DateTime dateUtc,
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);

        var path = BuildByServicePath(dateUtc, subscriptionId);
        var blob = _container.GetBlobClient(path);
        if (!await blob.ExistsAsync(cancellationToken))
            return new List<CostByServiceRow>();

        var response = await blob.DownloadContentAsync(cancellationToken);
        var json = response.Value.Content.ToString();
        var rows = JsonSerializer.Deserialize<List<CostByServiceRow>>(json, _jsonOptions) ?? new List<CostByServiceRow>();

        foreach (var row in rows)
        {
            row.SubscriptionId ??= subscriptionId;
        }

        return rows;
    }

    public async Task<List<CostByServiceRow>> LoadByServiceAllAsync(
        DateTime dateUtc,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);

        var prefix = BuildDatePrefixByService(dateUtc);
        var allRows = new List<CostByServiceRow>();

        await foreach (var blobItem in _container.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
        {
            if (!blobItem.Name.EndsWith("/byService.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var subscriptionId = ExtractSubscriptionFromPath(blobItem.Name) ?? string.Empty;
            var blob = _container.GetBlobClient(blobItem.Name);
            var response = await blob.DownloadContentAsync(cancellationToken);
            var json = response.Value.Content.ToString();
            var rows = JsonSerializer.Deserialize<List<CostByServiceRow>>(json, _jsonOptions) ?? new List<CostByServiceRow>();

            foreach (var row in rows)
            {
                row.SubscriptionId ??= subscriptionId;
            }

            allRows.AddRange(rows);
        }

        return allRows;
    }

    public async Task<bool> ExistsByServiceDataAsync(DateTime dateUtc, CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);

        var prefix = BuildDatePrefixByService(dateUtc);
        await foreach (var blobItem in _container.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
        {
            if (blobItem.Name.EndsWith("/byService.json", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public async Task SaveByResourceAsync(
        DateTime dateUtc,
        string subscriptionId,
        IReadOnlyCollection<CostByResourceRow> rows,
        string? rawJson = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);

        var byResourcePath = BuildByResourcePath(dateUtc, subscriptionId);
        var byResourceBlob = _container.GetBlobClient(byResourcePath);
        await using (var byResourceStream = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rows, _jsonOptions))))
        {
            await byResourceBlob.UploadAsync(byResourceStream, overwrite: true, cancellationToken: cancellationToken);
        }

        _logger.LogInformation(
            "Cost by resource salvo para {subscriptionId} em {date}: {path} ({count} linhas)",
            subscriptionId,
            dateUtc.ToString("yyyy-MM-dd"),
            byResourcePath,
            rows.Count);

        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            var rawPath = BuildRawResourcePath(dateUtc, subscriptionId);
            var rawBlob = _container.GetBlobClient(rawPath);
            await using var rawStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawJson));
            await rawBlob.UploadAsync(rawStream, overwrite: true, cancellationToken: cancellationToken);
        }
    }

    public async Task<List<CostByResourceRow>> LoadByResourceAsync(
        DateTime dateUtc,
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);

        var path = BuildByResourcePath(dateUtc, subscriptionId);
        var blob = _container.GetBlobClient(path);
        if (!await blob.ExistsAsync(cancellationToken))
            return new List<CostByResourceRow>();

        var response = await blob.DownloadContentAsync(cancellationToken);
        var json = response.Value.Content.ToString();
        var rows = JsonSerializer.Deserialize<List<CostByResourceRow>>(json, _jsonOptions) ?? new List<CostByResourceRow>();

        foreach (var row in rows)
        {
            row.SubscriptionId ??= subscriptionId;
        }

        return rows;
    }

    public async Task<List<CostByResourceRow>> LoadByResourceAllAsync(
        DateTime dateUtc,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);

        var prefix = BuildDatePrefixByResource(dateUtc);
        var allRows = new List<CostByResourceRow>();

        await foreach (var blobItem in _container.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
        {
            if (!blobItem.Name.EndsWith("/byResource.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var subscriptionId = ExtractSubscriptionFromPath(blobItem.Name) ?? string.Empty;
            var blob = _container.GetBlobClient(blobItem.Name);
            var response = await blob.DownloadContentAsync(cancellationToken);
            var json = response.Value.Content.ToString();
            var rows = JsonSerializer.Deserialize<List<CostByResourceRow>>(json, _jsonOptions) ?? new List<CostByResourceRow>();

            foreach (var row in rows)
            {
                row.SubscriptionId ??= subscriptionId;
            }

            allRows.AddRange(rows);
        }

        return allRows;
    }

    public async Task<bool> ExistsByResourceDataAsync(DateTime dateUtc, CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);

        var prefix = BuildDatePrefixByResource(dateUtc);
        await foreach (var blobItem in _container.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
        {
            if (blobItem.Name.EndsWith("/byResource.json", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public async Task<bool> CanAccessStorageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureContainerAsync(cancellationToken);
            await foreach (var _ in _container.GetBlobsAsync(traits: BlobTraits.None, states: BlobStates.None, prefix: null, cancellationToken: cancellationToken))
            {
                break;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao validar acesso ao storage de custo.");
            return false;
        }
    }

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
    }

    private static string BuildDatePrefixByService(DateTime dateUtc)
    {
        return $"cost/byService/date={dateUtc:yyyy-MM-dd}/";
    }

    private static string BuildByServicePath(DateTime dateUtc, string subscriptionId)
    {
        return $"{BuildDatePrefixByService(dateUtc)}subscriptionId={subscriptionId}/byService.json";
    }

    private static string BuildRawPath(DateTime dateUtc, string subscriptionId)
    {
        return $"{BuildDatePrefixByService(dateUtc)}subscriptionId={subscriptionId}/raw.json";
    }

    private static string BuildDatePrefixByResource(DateTime dateUtc)
    {
        return $"cost/byResource/date={dateUtc:yyyy-MM-dd}/";
    }

    private static string BuildByResourcePath(DateTime dateUtc, string subscriptionId)
    {
        return $"{BuildDatePrefixByResource(dateUtc)}subscriptionId={subscriptionId}/byResource.json";
    }

    private static string BuildRawResourcePath(DateTime dateUtc, string subscriptionId)
    {
        return $"{BuildDatePrefixByResource(dateUtc)}subscriptionId={subscriptionId}/raw.json";
    }

    private static string? ExtractSubscriptionFromPath(string path)
    {
        var parts = path.Split('/');
        var part = parts.FirstOrDefault(p => p.StartsWith("subscriptionId=", StringComparison.OrdinalIgnoreCase));
        if (part == null)
            return null;
        return part["subscriptionId=".Length..];
    }
}
