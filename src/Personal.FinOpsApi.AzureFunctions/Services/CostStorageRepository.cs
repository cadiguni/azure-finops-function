using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
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
            PropertyNameCaseInsensitive = true,
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
        {
            return await LoadExportByServiceAsync(
                dateUtc,
                subscriptionId,
                CostExportPublicationModes.Shadow,
                cancellationToken);
        }

        var response = await blob.DownloadContentAsync(cancellationToken);
        var json = response.Value.Content.ToString();
        var rows = JsonSerializer.Deserialize<List<CostByServiceRow>>(json, _jsonOptions) ?? new List<CostByServiceRow>();

        foreach (var row in rows)
        {
            row.SubscriptionId ??= subscriptionId;
        }

        return rows;
    }

    public async Task<List<CostByServiceRow>> LoadExportByServiceAsync(
        DateTime dateUtc,
        string subscriptionId,
        string publicationMode,
        CancellationToken cancellationToken = default)
    {
        var path = IsShadow(publicationMode)
            ? $"cost/shadow/byService/date={dateUtc:yyyy-MM-dd}/subscriptionId={subscriptionId}/byService.json"
            : BuildByServicePath(dateUtc, subscriptionId);
        var rows = await LoadRowsAsync<CostByServiceRow>(path, cancellationToken);
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
        var officialSubscriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var blobItem in _container.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
        {
            if (!blobItem.Name.EndsWith("/byService.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var subscriptionId = ExtractSubscriptionFromPath(blobItem.Name) ?? string.Empty;
            officialSubscriptions.Add(subscriptionId);
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

        var shadowPrefix = $"cost/shadow/byService/date={dateUtc:yyyy-MM-dd}/";
        await foreach (var blobItem in _container.GetBlobsAsync(prefix: shadowPrefix, cancellationToken: cancellationToken))
        {
            if (!blobItem.Name.EndsWith("/byService.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var subscriptionId = ExtractSubscriptionFromPath(blobItem.Name) ?? string.Empty;
            if (officialSubscriptions.Contains(subscriptionId))
                continue;

            var rows = await LoadRowsAsync<CostByServiceRow>(blobItem.Name, cancellationToken);
            foreach (var row in rows)
                row.SubscriptionId ??= subscriptionId;
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

    public Task SaveExportByServiceAsync(
        DateTime dateUtc,
        string subscriptionId,
        IReadOnlyCollection<CostByServiceRow> rows,
        string publicationMode,
        CancellationToken cancellationToken = default)
    {
        var path = IsShadow(publicationMode)
            ? $"cost/shadow/byService/date={dateUtc:yyyy-MM-dd}/subscriptionId={subscriptionId}/byService.json"
            : BuildByServicePath(dateUtc, subscriptionId);
        return SaveExportRowsAsync(path, rows, cancellationToken);
    }

    public Task SaveExportByResourceAsync(
        DateTime dateUtc,
        string subscriptionId,
        IReadOnlyCollection<CostByResourceRow> rows,
        string publicationMode,
        CancellationToken cancellationToken = default)
    {
        var path = IsShadow(publicationMode)
            ? $"cost/shadow/byResource/date={dateUtc:yyyy-MM-dd}/subscriptionId={subscriptionId}/byResource.json"
            : BuildByResourcePath(dateUtc, subscriptionId);
        return SaveExportRowsAsync(path, rows, cancellationToken);
    }

    public Task SaveExportAllocationAsync(
        DateTime dateUtc,
        string subscriptionId,
        IReadOnlyCollection<CostAllocationSummary> summaries,
        string publicationMode,
        CancellationToken cancellationToken = default)
    {
        var prefix = IsShadow(publicationMode) ? "cost/shadow/allocation" : "cost/allocation";
        var path = $"{prefix}/date={dateUtc:yyyy-MM-dd}/subscriptionId={subscriptionId}/allocation.json";
        return SaveExportRowsAsync(path, summaries, cancellationToken);
    }

    public async Task<List<CostAllocationSummary>> LoadAllocationAsync(
        DateTime dateUtc,
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        var path = $"cost/allocation/date={dateUtc:yyyy-MM-dd}/subscriptionId={subscriptionId}/allocation.json";
        await EnsureContainerAsync(cancellationToken);
        if (await _container.GetBlobClient(path).ExistsAsync(cancellationToken))
        {
            return await LoadRowsAsync<CostAllocationSummary>(path, cancellationToken);
        }

        return await LoadRowsAsync<CostAllocationSummary>(
                $"cost/shadow/allocation/date={dateUtc:yyyy-MM-dd}/subscriptionId={subscriptionId}/allocation.json",
                cancellationToken);
    }

    public async Task<List<CostAllocationSummary>> LoadAllocationAllAsync(
        DateTime dateUtc,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        var rows = new List<CostAllocationSummary>();
        var officialSubscriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = $"cost/allocation/date={dateUtc:yyyy-MM-dd}/";
        await foreach (var blobItem in _container.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
        {
            if (!blobItem.Name.EndsWith("/allocation.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            officialSubscriptions.Add(ExtractSubscriptionFromPath(blobItem.Name) ?? string.Empty);
            rows.AddRange(await LoadRowsAsync<CostAllocationSummary>(
                blobItem.Name,
                cancellationToken));
        }

        var shadowPrefix = $"cost/shadow/allocation/date={dateUtc:yyyy-MM-dd}/";
        await foreach (var blobItem in _container.GetBlobsAsync(prefix: shadowPrefix, cancellationToken: cancellationToken))
        {
            if (!blobItem.Name.EndsWith("/allocation.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var subscriptionId = ExtractSubscriptionFromPath(blobItem.Name) ?? string.Empty;
            if (!officialSubscriptions.Contains(subscriptionId))
                rows.AddRange(await LoadRowsAsync<CostAllocationSummary>(blobItem.Name, cancellationToken));
        }

        return rows;
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
        {
            return await LoadExportByResourceAsync(
                dateUtc,
                subscriptionId,
                CostExportPublicationModes.Shadow,
                cancellationToken);
        }

        var response = await blob.DownloadContentAsync(cancellationToken);
        var json = response.Value.Content.ToString();
        var rows = JsonSerializer.Deserialize<List<CostByResourceRow>>(json, _jsonOptions) ?? new List<CostByResourceRow>();

        foreach (var row in rows)
        {
            row.SubscriptionId ??= subscriptionId;
        }

        return rows;
    }

    public async Task<List<CostByResourceRow>> LoadExportByResourceAsync(
        DateTime dateUtc,
        string subscriptionId,
        string publicationMode,
        CancellationToken cancellationToken = default)
    {
        var path = IsShadow(publicationMode)
            ? $"cost/shadow/byResource/date={dateUtc:yyyy-MM-dd}/subscriptionId={subscriptionId}/byResource.json"
            : BuildByResourcePath(dateUtc, subscriptionId);
        var rows = await LoadRowsAsync<CostByResourceRow>(path, cancellationToken);
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
        var officialSubscriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var blobItem in _container.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
        {
            if (!blobItem.Name.EndsWith("/byResource.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var subscriptionId = ExtractSubscriptionFromPath(blobItem.Name) ?? string.Empty;
            officialSubscriptions.Add(subscriptionId);
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

        // During gradual cutover the importer writes validated Export snapshots below
        // cost/shadow. Expose those snapshots only for subscriptions without an official
        // (or legacy) blob on the same date so costs are never double counted.
        var shadowPrefix = $"cost/shadow/byResource/date={dateUtc:yyyy-MM-dd}/";
        await foreach (var blobItem in _container.GetBlobsAsync(prefix: shadowPrefix, cancellationToken: cancellationToken))
        {
            if (!blobItem.Name.EndsWith("/byResource.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var subscriptionId = ExtractSubscriptionFromPath(blobItem.Name) ?? string.Empty;
            if (officialSubscriptions.Contains(subscriptionId))
                continue;

            var rows = await LoadRowsAsync<CostByResourceRow>(blobItem.Name, cancellationToken);
            foreach (var row in rows)
                row.SubscriptionId ??= subscriptionId;
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

    public async Task<CostExportImportCheckpoint?> LoadImportCheckpointAsync(
        string subscriptionId,
        string exportName,
        string runId,
        string publicationMode,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        var blob = _container.GetBlobClient(BuildImportCheckpointPath(subscriptionId, exportName, runId, publicationMode));
        if (!await blob.ExistsAsync(cancellationToken))
        {
            if (!publicationMode.Equals(CostExportPublicationModes.Cutover, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            blob = _container.GetBlobClient(BuildLegacyImportCheckpointPath(subscriptionId, exportName, runId));
            if (!await blob.ExistsAsync(cancellationToken))
            {
                return null;
            }
        }

        var response = await blob.DownloadContentAsync(cancellationToken);
        var checkpoint = JsonSerializer.Deserialize<CostExportImportCheckpoint>(
            response.Value.Content.ToString(),
            _jsonOptions);

        if (checkpoint is null)
        {
            throw new InvalidDataException($"Checkpoint de importação inválido: '{blob.Name}'.");
        }

        return checkpoint.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ? checkpoint : null;
    }

    public async Task<IAsyncDisposable?> TryAcquireExportPublicationLeaseAsync(
        string subscriptionId,
        string exportName,
        string publicationMode,
        DateTime? maxUsageDate,
        DateTimeOffset submittedTime,
        string runId,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        var lockBlob = _container.GetBlobClient(BuildPublicationLockPath(subscriptionId, publicationMode));
        try
        {
            await lockBlob.UploadAsync(
                BinaryData.FromString(string.Empty),
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
                },
                cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            // O blob de coordenação já existe; a exclusão mútua é controlada pelo lease.
        }

        var leaseClient = lockBlob.GetBlobLeaseClient();
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                await leaseClient.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);
                try
                {
                    // Lease is mutual exclusion only. Historical corrections are ordered per day.

                    return new BlobPublicationLease(leaseClient);
                }
                catch
                {
                    await leaseClient.ReleaseAsync(cancellationToken: CancellationToken.None);
                    throw;
                }
            }
            catch (RequestFailedException exception) when (
                exception.Status is 409 or 412 && attempt < 39)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Não foi possível adquirir o lease de publicação para subscriptionId={subscriptionId}, mode={publicationMode}.");
    }

    public async Task SaveImportRunCheckpointAsync(
        CostExportImportCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        var path = BuildImportCheckpointPath(
            checkpoint.SubscriptionId,
            checkpoint.ExportName,
            checkpoint.RunId,
            checkpoint.PublicationMode);
        var blob = _container.GetBlobClient(path);
        try
        {
            await blob.UploadAsync(
                BinaryData.FromString(JsonSerializer.Serialize(checkpoint, _jsonOptions)),
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
                },
                cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            var existing = await LoadImportCheckpointAsync(
                checkpoint.SubscriptionId,
                checkpoint.ExportName,
                checkpoint.RunId,
                checkpoint.PublicationMode,
                cancellationToken);
            if (existing is null)
            {
                throw;
            }
        }

        _logger.LogInformation(
            "Checkpoint de importação salvo: subscriptionId={subscriptionId} exportName={exportName} runId={runId} path={path}",
            checkpoint.SubscriptionId,
            checkpoint.ExportName,
            checkpoint.RunId,
            path);
    }

    public async Task UpdateLatestImportCheckpointAsync(
        CostExportImportCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        await UploadLatestIfNewerAsync(
            BuildLatestImportCheckpointPath(
                checkpoint.SubscriptionId,
                checkpoint.ExportName,
                checkpoint.PublicationMode),
            checkpoint,
            value => value.MaxUsageDate,
            value => value.SubmittedTime,
            value => value.RunId,
            cancellationToken);
    }

    public async Task<CostExportImportCheckpoint?> LoadLatestCompletedImportCheckpointAsync(
        string subscriptionId,
        string exportName,
        string publicationMode,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        var blob = _container.GetBlobClient(
            BuildLatestImportCheckpointPath(subscriptionId, exportName, publicationMode));
        if (!await blob.ExistsAsync(cancellationToken))
        {
            if (!publicationMode.Equals(CostExportPublicationModes.Cutover, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            blob = _container.GetBlobClient(BuildLegacyLatestImportCheckpointPath(subscriptionId, exportName));
            if (!await blob.ExistsAsync(cancellationToken))
            {
                return null;
            }
        }

        var response = await blob.DownloadContentAsync(cancellationToken);
        var checkpoint = JsonSerializer.Deserialize<CostExportImportCheckpoint>(
            response.Value.Content.ToString(),
            _jsonOptions);
        if (checkpoint is null ||
            !checkpoint.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Checkpoint mais recente inválido: '{blob.Name}'.");
        }

        return checkpoint;
    }

    public async Task SavePublicationWatermarkAsync(
        CostExportPublicationWatermark watermark,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        var path = BuildPublicationWatermarkPath(watermark.SubscriptionId, watermark.PublicationMode);
        await UploadLatestIfNewerAsync(
            path,
            watermark,
            value => value.MaxUsageDate,
            value => value.SubmittedTime,
            value => value.RunId,
            cancellationToken);

        _logger.LogInformation(
            "Watermark de custo salvo: subscriptionId={subscriptionId} source={source} runId={runId} minUsageDate={minUsageDate} maxUsageDate={maxUsageDate} status={status} path={path}",
            watermark.SubscriptionId,
            watermark.Source,
            watermark.RunId,
            watermark.MinUsageDate?.ToString("yyyy-MM-dd"),
            watermark.MaxUsageDate?.ToString("yyyy-MM-dd"),
            watermark.Status,
            path);
    }

    public async Task<CostExportPublicationWatermark?> LoadPublicationWatermarkAsync(
        string subscriptionId,
        string publicationMode,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        var blob = _container.GetBlobClient(BuildPublicationWatermarkPath(subscriptionId, publicationMode));
        if (!await blob.ExistsAsync(cancellationToken))
        {
            return null;
        }

        var response = await blob.DownloadContentAsync(cancellationToken);
        return JsonSerializer.Deserialize<CostExportPublicationWatermark>(
            response.Value.Content.ToString(),
            _jsonOptions);
    }

    public async Task<CostExportImportCursor?> LoadImportCursorAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        var blob = _container.GetBlobClient(BuildImportCursorPath());
        if (!await blob.ExistsAsync(cancellationToken))
        {
            return null;
        }

        var response = await blob.DownloadContentAsync(cancellationToken);
        return JsonSerializer.Deserialize<CostExportImportCursor>(
            response.Value.Content.ToString(),
            _jsonOptions);
    }

    public async Task SaveImportCursorAsync(
        CostExportImportCursor cursor,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        await _container.GetBlobClient(BuildImportCursorPath()).UploadAsync(
            BinaryData.FromString(JsonSerializer.Serialize(cursor, _jsonOptions)),
            overwrite: true,
            cancellationToken: cancellationToken);
    }

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
    }

    private async Task SaveExportRowsAsync<TRow>(
        string path,
        IReadOnlyCollection<TRow> rows,
        CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);
        var blob = _container.GetBlobClient(path);
        await blob.UploadAsync(
            BinaryData.FromString(JsonSerializer.Serialize(rows, _jsonOptions)),
            overwrite: true,
            cancellationToken: cancellationToken);
    }

    private async Task<List<TRow>> LoadRowsAsync<TRow>(
        string path,
        CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);
        var blob = _container.GetBlobClient(path);
        if (!await blob.ExistsAsync(cancellationToken))
        {
            return [];
        }

        var response = await blob.DownloadContentAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<TRow>>(response.Value.Content.ToString(), _jsonOptions) ?? [];
    }

    private static bool IsShadow(string publicationMode)
    {
        if (publicationMode.Equals(CostExportPublicationModes.Shadow, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (publicationMode.Equals(CostExportPublicationModes.Cutover, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new ArgumentException("Modo de publicação de export inválido.", nameof(publicationMode));
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

    private static string BuildImportCheckpointPath(
        string subscriptionId,
        string exportName,
        string runId,
        string publicationMode)
    {
        ValidatePathSegment(subscriptionId, nameof(subscriptionId));
        ValidatePathSegment(exportName, nameof(exportName));
        ValidatePathSegment(runId, nameof(runId));
        ValidatePathSegment(publicationMode, nameof(publicationMode));
        return $"cost/imports/checkpoints/mode={publicationMode.ToLowerInvariant()}/subscriptionId={subscriptionId}/exportName={exportName}/runId={runId}.json";
    }

    private static string BuildLatestImportCheckpointPath(
        string subscriptionId,
        string exportName,
        string publicationMode)
    {
        ValidatePathSegment(subscriptionId, nameof(subscriptionId));
        ValidatePathSegment(exportName, nameof(exportName));
        ValidatePathSegment(publicationMode, nameof(publicationMode));
        return $"cost/imports/checkpoints/mode={publicationMode.ToLowerInvariant()}/subscriptionId={subscriptionId}/exportName={exportName}/latest.json";
    }

    private static string BuildPublicationWatermarkPath(string subscriptionId, string publicationMode)
    {
        ValidatePathSegment(subscriptionId, nameof(subscriptionId));
        ValidatePathSegment(publicationMode, nameof(publicationMode));
        return $"cost/imports/watermarks/mode={publicationMode.ToLowerInvariant()}/subscriptionId={subscriptionId}/latest.json";
    }

    private static string BuildPublicationLockPath(string subscriptionId, string publicationMode)
    {
        ValidatePathSegment(subscriptionId, nameof(subscriptionId));
        ValidatePathSegment(publicationMode, nameof(publicationMode));
        return $"cost/imports/locks/mode={publicationMode.ToLowerInvariant()}/subscriptionId={subscriptionId}.lock";
    }

    private static string BuildImportCursorPath() => "cost/imports/cursors/manifests.json";

    private static string BuildLegacyImportCheckpointPath(string subscriptionId, string exportName, string runId)
    {
        return $"cost/imports/checkpoints/subscriptionId={subscriptionId}/exportName={exportName}/runId={runId}.json";
    }

    private static string BuildLegacyLatestImportCheckpointPath(string subscriptionId, string exportName)
    {
        return $"cost/imports/checkpoints/subscriptionId={subscriptionId}/exportName={exportName}/latest.json";
    }

    private async Task UploadLatestIfNewerAsync<T>(
        string path,
        T candidate,
        Func<T, DateTime?> maxUsageDate,
        Func<T, DateTimeOffset> submittedTime,
        Func<T, string> runId,
        CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient(path);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ETag? etag = null;
            if (await blob.ExistsAsync(cancellationToken))
            {
                var response = await blob.DownloadContentAsync(cancellationToken);
                var current = JsonSerializer.Deserialize<T>(response.Value.Content.ToString(), _jsonOptions)
                    ?? throw new InvalidDataException($"Documento de controle inválido: '{path}'.");
                etag = response.Value.Details.ETag;
                if (CompareControlDocuments(
                        maxUsageDate(candidate),
                        submittedTime(candidate),
                        runId(candidate),
                        maxUsageDate(current),
                        submittedTime(current),
                        runId(current)) < 0)
                {
                    return;
                }
            }

            try
            {
                await blob.UploadAsync(
                    BinaryData.FromString(JsonSerializer.Serialize(candidate, _jsonOptions)),
                    new BlobUploadOptions
                    {
                        Conditions = etag.HasValue
                            ? new BlobRequestConditions { IfMatch = etag.Value }
                            : new BlobRequestConditions { IfNoneMatch = ETag.All }
                    },
                    cancellationToken);
                return;
            }
            catch (RequestFailedException exception) when (exception.Status is 409 or 412 && attempt < 4)
            {
                // Outro importador atualizou o ponteiro; releia e compare antes de tentar novamente.
            }
        }

        throw new InvalidOperationException($"Não foi possível atualizar '{path}' após concorrência repetida.");
    }

    internal static int CompareRuns(
        DateTimeOffset leftSubmittedTime,
        string leftRunId,
        DateTimeOffset rightSubmittedTime,
        string rightRunId)
    {
        var comparison = leftSubmittedTime.CompareTo(rightSubmittedTime);
        return comparison != 0
            ? comparison
            : StringComparer.OrdinalIgnoreCase.Compare(leftRunId, rightRunId);
    }

    internal static int CompareControlDocuments(
        DateTime? leftMaxUsageDate,
        DateTimeOffset leftSubmittedTime,
        string leftRunId,
        DateTime? rightMaxUsageDate,
        DateTimeOffset rightSubmittedTime,
        string rightRunId)
    {
        if (leftMaxUsageDate.HasValue != rightMaxUsageDate.HasValue)
        {
            return leftMaxUsageDate.HasValue ? 1 : -1;
        }

        if (leftMaxUsageDate.HasValue)
        {
            var usageDateComparison = DateTime.Compare(
                leftMaxUsageDate.Value.Date,
                rightMaxUsageDate!.Value.Date);
            if (usageDateComparison != 0)
            {
                return usageDateComparison;
            }
        }

        return CompareRuns(leftSubmittedTime, leftRunId, rightSubmittedTime, rightRunId);
    }

    private static void ValidatePathSegment(string value, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("O valor contém caracteres inválidos para um path de checkpoint.", argumentName);
        }
    }

    private sealed class BlobPublicationLease : IAsyncDisposable
    {
        private BlobLeaseClient? _leaseClient;
        private readonly CancellationTokenSource _renewalCancellation = new();
        private readonly Task _renewalTask;

        public BlobPublicationLease(BlobLeaseClient leaseClient)
        {
            _leaseClient = leaseClient;
            _renewalTask = RenewUntilDisposedAsync(leaseClient, _renewalCancellation.Token);
        }

        public async ValueTask DisposeAsync()
        {
            var leaseClient = Interlocked.Exchange(ref _leaseClient, null);
            if (leaseClient is not null)
            {
                await _renewalCancellation.CancelAsync();
                try
                {
                    await _renewalTask;
                }
                catch (OperationCanceledException)
                {
                    // Encerramento esperado da renovação ao concluir a promoção.
                }
                catch (RequestFailedException)
                {
                    // A liberação abaixo confirma o estado final quando uma renovação falhou.
                }

                try
                {
                    await leaseClient.ReleaseAsync(cancellationToken: CancellationToken.None);
                }
                catch (RequestFailedException exception) when (exception.Status is 409 or 412)
                {
                    // O lease já expirou ou foi perdido; não há lock remanescente para liberar.
                }
            }

            _renewalCancellation.Dispose();
        }

        private static async Task RenewUntilDisposedAsync(
            BlobLeaseClient leaseClient,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(40), cancellationToken);
                await leaseClient.RenewAsync(cancellationToken: cancellationToken);
            }
        }
    }
}
