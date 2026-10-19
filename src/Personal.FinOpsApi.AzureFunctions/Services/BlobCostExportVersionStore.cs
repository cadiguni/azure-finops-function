using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>Immutable payloads plus a single CAS-protected publication index per subscription/mode.</summary>
public sealed class BlobCostExportVersionStore : ICostExportVersionStore
{
    private readonly BlobContainerClient _container;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public BlobCostExportVersionStore(BlobServiceClient blobs, IConfiguration configuration)
        => _container = blobs.GetBlobContainerClient(configuration["COST_STORAGE_CONTAINER"] ?? "finops-analysis");

    private static string Prefix(string subscriptionId, string mode)
        => $"cost/versions/{CostExportDailySnapshot.Hash(subscriptionId.ToLowerInvariant() + "|" + mode)}";

    public async Task<CostExportRevisionIndex?> LoadIndexAsync(string subscriptionId, string mode, CancellationToken cancellationToken = default)
    {
        var value = await ReadAsync<CostExportRevisionIndex>($"{Prefix(subscriptionId, mode)}/latest.json", cancellationToken);
        return value.Value;
    }

    public async Task<CostExportDailySnapshot> LoadSnapshotAsync(string subscriptionId, string mode, string revision, CancellationToken cancellationToken = default)
    {
        if (revision.Length != 64 || !revision.All(Uri.IsHexDigit)) throw new InvalidDataException("Revisão inválida.");
        var path = $"{Prefix(subscriptionId, mode)}/days/{revision}.json";
        var blob = _container.GetBlobClient(path);
        BinaryData content;
        try
        {
            content = (await blob.DownloadContentAsync(cancellationToken)).Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new InvalidDataException("Snapshot referenciado não encontrado.", ex);
        }

        using var document = JsonDocument.Parse(content);
        var persistedRevision = document.RootElement.TryGetProperty("revision", out var revisionElement)
            ? revisionElement.GetString()
            : null;
        var snapshot = content.ToObjectFromJson<CostExportDailySnapshot>(Json)
            ?? throw new InvalidDataException($"Documento inválido: {path}");
        if (!IsSnapshotReferenceValid(snapshot, persistedRevision, subscriptionId, mode, revision))
            throw new InvalidDataException("Integridade do snapshot inválida.");
        return snapshot;
    }

    internal static bool IsSnapshotReferenceValid(
        CostExportDailySnapshot snapshot,
        string? persistedRevision,
        string subscriptionId,
        string mode,
        string requestedRevision)
    {
        // Revision used to be derived from the runtime JSON contract. Adding JsonPropertyName to
        // nested rows changed the recalculated hash even though the immutable payload did not
        // change. Prefer the revision written into the immutable document for compatibility;
        // snapshots created before that field existed still use the calculated value.
        var effectiveRevision = string.IsNullOrWhiteSpace(persistedRevision)
            ? snapshot.Revision
            : persistedRevision;
        return string.Equals(effectiveRevision, requestedRevision, StringComparison.OrdinalIgnoreCase)
            && snapshot.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(snapshot.PublicationMode, mode, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReceiptPath(string subscriptionId, string mode, string exportName, string runId)
        => $"{Prefix(subscriptionId, mode)}/runs/{CostExportDailySnapshot.Hash(exportName + "|" + runId)}.json";

    public async Task<bool> HasPublishedRunAsync(string subscriptionId, string mode, string exportName, string runId, CancellationToken cancellationToken = default)
        => (await _container.GetBlobClient(ReceiptPath(subscriptionId, mode, exportName, runId)).ExistsAsync(cancellationToken)).Value;

    public async Task PublishAsync(CostExportImportCheckpoint checkpoint, IReadOnlyList<CostExportDailySnapshot> snapshots, CancellationToken cancellationToken = default)
    {
        await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        var prefix = Prefix(checkpoint.SubscriptionId, checkpoint.PublicationMode);
        foreach (var snapshot in snapshots)
            await WriteImmutableAsync($"{prefix}/days/{snapshot.Revision}.json", snapshot, cancellationToken);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var (current, etag) = await ReadAsync<CostExportRevisionIndex>($"{prefix}/latest.json", cancellationToken);
            current ??= new CostExportRevisionIndex { SubscriptionId = checkpoint.SubscriptionId, PublicationMode = checkpoint.PublicationMode };
            var merged = current.Merge(snapshots);
            await WriteImmutableAsync($"{prefix}/indexes/{merged.Revision}.json", merged, cancellationToken);
            try
            {
                await _container.GetBlobClient($"{prefix}/latest.json").UploadAsync(BinaryData.FromObjectAsJson(merged, Json),
                    new BlobUploadOptions { Conditions = etag.HasValue ? new BlobRequestConditions { IfMatch = etag.Value }
                        : new BlobRequestConditions { IfNoneMatch = ETag.All } }, cancellationToken);
                // Receipt only after all payloads and the index have become visible.
                await WriteImmutableAsync(ReceiptPath(checkpoint.SubscriptionId, checkpoint.PublicationMode, checkpoint.ExportName, checkpoint.RunId),
                    new { indexRevision = merged.Revision, checkpoint.RunId }, cancellationToken);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412 && attempt < 9) { }
        }
        throw new InvalidOperationException("Publicação concorrente não pôde ser concluída.");
    }

    private async Task WriteImmutableAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        try
        {
            await _container.GetBlobClient(path).UploadAsync(BinaryData.FromObjectAsJson(value, Json),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412) { }
    }

    private async Task<(T? Value, ETag? ETag)> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _container.GetBlobClient(path).DownloadContentAsync(cancellationToken);
            return (response.Value.Content.ToObjectFromJson<T>(Json) ?? throw new InvalidDataException($"Documento inválido: {path}"), response.Value.Details.ETag);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return (default, null); }
    }
}
