using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public sealed class BlobCostExportBlobSource : ICostExportBlobSource
{
    private readonly BlobContainerClient _container;

    public BlobCostExportBlobSource(
        BlobServiceClient defaultBlobServiceClient,
        TokenCredential credential,
        IConfiguration configuration,
        ILogger<BlobCostExportBlobSource> logger)
    {
        var exportBlobServiceClient = ResolveExportBlobServiceClient(
            defaultBlobServiceClient,
            credential,
            configuration,
            logger);
        var containerName = configuration["COST_EXPORT_CONTAINER"] ?? "cost-exports";
        _container = exportBlobServiceClient.GetBlobContainerClient(containerName);
    }

    public async IAsyncEnumerable<string> ListManifestNamesAsync(
        IReadOnlyCollection<string> subscriptionIds,
        int maximumResults,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (subscriptionIds.Count == 0 || maximumResults <= 0)
        {
            yield break;
        }

        var prefixes = subscriptionIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(subscriptionId => $"subscriptions/{subscriptionId}/billing-period/current/");
        var enumerators = new Queue<IAsyncEnumerator<string>>(
            prefixes.Select(prefix => EnumerateManifestNamesAsync(prefix, cancellationToken)
                .GetAsyncEnumerator(cancellationToken)));
        var returned = 0;
        try
        {
            while (enumerators.Count > 0 && returned < maximumResults)
            {
                var enumerator = enumerators.Dequeue();
                if (await enumerator.MoveNextAsync())
                {
                    yield return enumerator.Current;
                    returned++;
                    enumerators.Enqueue(enumerator);
                }
                else
                {
                    await enumerator.DisposeAsync();
                }
            }
        }
        finally
        {
            while (enumerators.Count > 0)
            {
                await enumerators.Dequeue().DisposeAsync();
            }
        }
    }

    public async IAsyncEnumerable<string> ListSubscriptionIdsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const string rootPrefix = "subscriptions/";
        await foreach (var item in _container.GetBlobsByHierarchyAsync(
                           delimiter: "/",
                           prefix: rootPrefix,
                           cancellationToken: cancellationToken))
        {
            if (!item.IsPrefix || string.IsNullOrWhiteSpace(item.Prefix))
            {
                continue;
            }

            var candidate = item.Prefix[rootPrefix.Length..].TrimEnd('/');
            if (Guid.TryParse(candidate, out _))
            {
                yield return candidate;
            }
        }
    }

    private async IAsyncEnumerable<string> EnumerateManifestNamesAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var manifests = new List<ManifestBlobCandidate>();
        await foreach (var blob in _container.GetBlobsAsync(
                           prefix: prefix,
                           cancellationToken: cancellationToken))
        {
            if (blob.Name.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                manifests.Add(new ManifestBlobCandidate(
                    blob.Name,
                    blob.Properties.LastModified ?? DateTimeOffset.MinValue));
            }
        }

        foreach (var manifestName in OrderManifestNamesNewestFirst(manifests))
        {
            yield return manifestName;
        }
    }

    internal static IReadOnlyList<string> OrderManifestNamesNewestFirst(
        IEnumerable<ManifestBlobCandidate> manifests) =>
        manifests
            .OrderByDescending(manifest => GetBillingPeriodEnd(manifest.Name))
            .ThenByDescending(manifest => manifest.LastModified)
            .ThenByDescending(manifest => manifest.Name, StringComparer.Ordinal)
            .Select(manifest => manifest.Name)
            .ToList();

    private static DateTime GetBillingPeriodEnd(string manifestName)
    {
        foreach (var segment in manifestName.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length == 17 &&
                segment[8] == '-' &&
                DateTime.TryParseExact(
                    segment.AsSpan(9, 8),
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var periodEnd))
            {
                return periodEnd;
            }
        }

        return DateTime.MinValue;
    }

    public async Task<string> ReadTextAsync(
        string blobName,
        CancellationToken cancellationToken = default)
    {
        var response = await _container.GetBlobClient(blobName).DownloadContentAsync(cancellationToken);
        return response.Value.Content.ToString();
    }

    public async Task<Stream> OpenReadAsync(
        string blobName,
        CancellationToken cancellationToken = default)
    {
        var response = await _container.GetBlobClient(blobName).DownloadStreamingAsync(cancellationToken: cancellationToken);
        return response.Value.Content;
    }

    private static BlobServiceClient ResolveExportBlobServiceClient(
        BlobServiceClient defaultBlobServiceClient,
        TokenCredential credential,
        IConfiguration configuration,
        ILogger logger)
    {
        var serviceUri = configuration["COST_EXPORT_STORAGE_ACCOUNT_URI"]?.Trim();
        if (!string.IsNullOrWhiteSpace(serviceUri))
        {
            if (!Uri.TryCreate(serviceUri, UriKind.Absolute, out var parsedUri) ||
                !string.Equals(parsedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "COST_EXPORT_STORAGE_ACCOUNT_URI deve ser uma URI HTTPS absoluta.");
            }

            logger.LogInformation(
                "Storage de Cost Management Exports configurado separadamente com Managed Identity: host={host}",
                parsedUri.Host);
            return new BlobServiceClient(parsedUri, credential);
        }

        var localConnectionString = configuration["COST_EXPORT_STORAGE_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(localConnectionString))
        {
            logger.LogInformation(
                "Storage de Cost Management Exports configurado separadamente por connection string local.");
            return new BlobServiceClient(localConnectionString);
        }

        logger.LogInformation(
            "Storage de Cost Management Exports compartilha o BlobServiceClient de AzureWebJobsStorage.");
        return defaultBlobServiceClient;
    }

    internal readonly record struct ManifestBlobCandidate(
        string Name,
        DateTimeOffset LastModified);
}
