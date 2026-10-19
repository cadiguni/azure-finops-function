using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public sealed class CostExportImportService : ICostExportImportService
{
    internal const string SupportedExportName = "finops-actual-cost-current-billing";

    private readonly ICostExportBlobSource _exportSource;
    private readonly ICostStorageRepository _costStorageRepository;
    private readonly ICostExportVersionStore _versions;
    private readonly CostExportCsvParser _csvParser;
    private readonly CostExportManifestParser _manifestParser;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostExportImportService> _logger;
    private readonly string _costAllocationTagName;

    public CostExportImportService(
        ICostExportBlobSource exportSource,
        ICostStorageRepository costStorageRepository,
        ICostExportVersionStore versions,
        CostExportCsvParser csvParser,
        CostExportManifestParser manifestParser,
        IConfiguration configuration,
        ILogger<CostExportImportService> logger)
    {
        _exportSource = exportSource;
        _costStorageRepository = costStorageRepository;
        _versions = versions;
        _csvParser = csvParser;
        _manifestParser = manifestParser;
        _configuration = configuration;
        _logger = logger;
        _costAllocationTagName = _configuration["CostAllocationTagName"] ?? "CentrodeCusto";
    }

    public async Task<CostExportImportResult> ImportLatestAsync(
        CancellationToken cancellationToken = default)
    {
        CostCollectionRouting.ValidateExportConfiguration(_configuration);
        if (!_configuration.GetValue(CostCollectionRouting.EnableExportImportSetting, false))
        {
            return new CostExportImportResult();
        }

        var ingestionSubscriptions = await ResolveIngestionSubscriptionsAsync(cancellationToken);
        if (ingestionSubscriptions.Count == 0)
        {
            _logger.LogWarning(
                "Importação de exports habilitada, mas nenhuma subscription foi encontrada na configuração ou no container.");
            return new CostExportImportResult();
        }

        var failures = new List<string>();
        var manifestsFound = 0;
        var processedManifests = 0;
        var skippedManifests = 0;
        var processedPartitions = 0;
        long importedRows = 0;
        var publishedDates = 0;
        var processedSubscriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var analysisTargets = new List<CostExportAnalysisTarget>();
        var latestCompletedByExport =
            new Dictionary<string, CostExportImportCheckpoint?>(StringComparer.OrdinalIgnoreCase);

        var cursor = await _costStorageRepository.LoadImportCursorAsync(cancellationToken);
        var orderedSubscriptions = RotateSubscriptions(ingestionSubscriptions, cursor?.LastSubscriptionId);
        string? lastVisitedSubscription = null;
        var maximumManifests = GetMaximumManifestsPerRun();
        await foreach (var manifestName in _exportSource.ListManifestNamesAsync(
                           orderedSubscriptions,
                           maximumManifests,
                           cancellationToken))
        {
            if (!TryExtractCurrentSubscriptionId(manifestName, out var subscriptionId) ||
                !ingestionSubscriptions.Contains(subscriptionId))
            {
                continue;
            }

            lastVisitedSubscription = subscriptionId;
            manifestsFound++;
            string? exportName = null;
            string? runId = null;

            try
            {
                var json = await _exportSource.ReadTextAsync(manifestName, cancellationToken);
                var manifest = _manifestParser.Parse(json, manifestName);
                exportName = manifest.ExportConfig.ExportName.Trim();
                runId = manifest.RunInfo.RunId.Trim();
                ValidateManifestStructure(manifest, manifestName, subscriptionId);
                var publicationMode = CostCollectionRouting.IsCutoverSubscription(
                    _configuration,
                    subscriptionId)
                    ? CostExportPublicationModes.Cutover
                    : CostExportPublicationModes.Shadow;
                var completedRun = await _costStorageRepository.LoadImportCheckpointAsync(
                        subscriptionId,
                        exportName,
                        runId,
                        publicationMode,
                        cancellationToken);
                if (completedRun is not null && await _versions.HasPublishedRunAsync(subscriptionId, publicationMode, exportName, runId, cancellationToken))
                {
                    await ReconcileControlDocumentsAsync(completedRun, cancellationToken);
                    skippedManifests++;
                    _logger.LogInformation(
                        "Manifesto reconciliado por checkpoint concluído: subscriptionId={subscriptionId} exportName={exportName} runId={runId} mode={mode} manifest={manifest}",
                        subscriptionId,
                        exportName,
                        runId,
                        publicationMode,
                        manifestName);
                    continue;
                }

                var exportKey = $"{publicationMode}|{subscriptionId}|{exportName}";
                if (!latestCompletedByExport.TryGetValue(exportKey, out var latestCompleted))
                {
                    latestCompleted = await _costStorageRepository.LoadLatestCompletedImportCheckpointAsync(
                        subscriptionId,
                        exportName,
                        publicationMode,
                        cancellationToken);
                    latestCompletedByExport[exportKey] = latestCompleted;
                }

                // Ordering is per usage date in the revision index, not by the latest global run.

                ValidateCurrentExportContract(manifest, manifestName);
                var candidate = new ManifestCandidate(
                    manifestName,
                    subscriptionId,
                    manifest,
                    publicationMode);
                var outcome = await ProcessManifestAsync(candidate, cancellationToken);
                if (outcome.Promoted)
                {
                    processedManifests++;
                    processedPartitions += outcome.PartitionsProcessed;
                    importedRows += outcome.RowsProcessed;
                    publishedDates += outcome.PublishedDates;
                    processedSubscriptions.Add(candidate.SubscriptionId);
                    analysisTargets.Add(new CostExportAnalysisTarget(candidate.SubscriptionId, candidate.Manifest.RunInfo.RunId));
                    if (outcome.ControlDocumentsPromoted)
                    {
                        latestCompletedByExport[exportKey] = outcome.Checkpoint;
                    }
                }
                else
                {
                    skippedManifests++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failure = BuildFailure(
                    subscriptionId,
                    exportName,
                    runId,
                    manifestName,
                    ex.Message);
                failures.Add(failure);
                _logger.LogError(ex, "Falha ao processar {failure}", failure);
            }
        }

        if (lastVisitedSubscription is not null)
        {
            await _costStorageRepository.SaveImportCursorAsync(
                new CostExportImportCursor
                {
                    LastSubscriptionId = lastVisitedSubscription,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                cancellationToken);
        }

        var result = new CostExportImportResult
        {
            ManifestsFound = manifestsFound,
            ManifestsProcessed = processedManifests,
            ManifestsSkippedByCheckpoint = skippedManifests,
            SubscriptionsProcessed = processedSubscriptions.Count,
            PartitionsProcessed = processedPartitions,
            ImportedRows = importedRows,
            PublishedDates = publishedDates,
            AnalysisTargets = analysisTargets
                .DistinctBy(target => $"{target.SubscriptionId}|{target.RunId}", StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Failures = failures
        };

        if (failures.Count > 0)
        {
            throw new CostExportImportException(result);
        }

        return result;
    }

    private async Task ReconcileControlDocumentsAsync(
        CostExportImportCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await using var publicationLease = await _costStorageRepository.TryAcquireExportPublicationLeaseAsync(
            checkpoint.SubscriptionId,
            checkpoint.ExportName,
            checkpoint.PublicationMode,
            checkpoint.MaxUsageDate,
            checkpoint.SubmittedTime,
            checkpoint.RunId,
            cancellationToken);
        if (publicationLease is null)
        {
            return;
        }

        if (!await ShouldPromoteControlDocumentsAsync(checkpoint, cancellationToken))
        {
            return;
        }

        await _costStorageRepository.UpdateLatestImportCheckpointAsync(checkpoint, cancellationToken);
        await _costStorageRepository.SavePublicationWatermarkAsync(
            CreateWatermark(checkpoint),
            cancellationToken);
    }

    private async Task<ManifestOutcome> ProcessManifestAsync(
        ManifestCandidate candidate,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var processingStartedAt = DateTimeOffset.UtcNow;
        var manifest = candidate.Manifest;
        var exportName = manifest.ExportConfig.ExportName.Trim();
        var runId = manifest.RunInfo.RunId.Trim();
        var serviceAggregates = new Dictionary<ServiceAggregateKey, AggregateValue>();
        var resourceAggregates = new Dictionary<ResourceAggregateKey, AggregateValue>();
        var dailyTotals = new Dictionary<DailyCurrencyKey, decimal>();
        var dailyAllocatedTotals = new Dictionary<DailyCurrencyKey, decimal>();
        var totalsByCurrency = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        long totalRows = 0;
        var partitionsProcessed = 0;

        foreach (var partition in manifest.Blobs)
        {
            ValidatePartitionPath(partition.BlobName, candidate.SubscriptionId, exportName);
            var partitionStopwatch = Stopwatch.StartNew();
            long partitionRows = 0;

            try
            {
                await using var blobStream = await _exportSource.OpenReadAsync(
                    partition.BlobName,
                    cancellationToken);
                await using var dataStream = CreateDataStream(
                    blobStream,
                    manifest.DeliveryConfig.CompressionMode,
                    partition.BlobName);
                using var reader = new StreamReader(
                    dataStream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 1024,
                    leaveOpen: false);

                foreach (var record in _csvParser.Parse(reader, candidate.SubscriptionId))
                {
                    Add(serviceAggregates, new ServiceAggregateKey(
                        record.UsageDate.Date,
                        record.ServiceName,
                        record.Currency), record.Cost);

                    var dailyCurrencyKey = new DailyCurrencyKey(record.UsageDate.Date, record.Currency);
                    AddCost(dailyTotals, dailyCurrencyKey, record.Cost);

                    if (!string.IsNullOrWhiteSpace(record.ResourceId))
                    {
                        Add(resourceAggregates, new ResourceAggregateKey(
                            record.UsageDate.Date,
                            record.ResourceId,
                            record.ServiceName,
                            record.Currency,
                            record.ResourceGroup,
                            record.ResourceLocation,
                            record.ResourceType,
                            record.Tags), record.Cost);
                        AddCost(dailyAllocatedTotals, dailyCurrencyKey, record.Cost);
                    }

                    AddCurrencyTotal(totalsByCurrency, record.Currency, record.Cost);

                    partitionRows++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException(
                    $"Falha na partição '{partition.BlobName}': {ex.Message}",
                    ex);
            }

            if (partitionRows != partition.DataRowCount)
            {
                throw new InvalidDataException(
                    $"Partição '{partition.BlobName}' declarou {partition.DataRowCount} linhas, mas {partitionRows} linhas válidas foram lidas.");
            }

            totalRows += partitionRows;
            partitionsProcessed++;
            _logger.LogInformation(
                "Partição processada: subscriptionId={subscriptionId} exportName={exportName} runId={runId} manifest={manifest} partition={partition} rows={rows} durationMs={durationMs}",
                candidate.SubscriptionId,
                exportName,
                runId,
                candidate.ManifestName,
                partition.BlobName,
                partitionRows,
                partitionStopwatch.ElapsedMilliseconds);
        }

        ValidateManifestTotals(manifest, partitionsProcessed, totalRows);

        var dates = serviceAggregates.Keys
            .Select(key => key.Date)
            .Distinct()
            .Order()
            .ToList();
        var publishedBlobs = new List<string>(dates.Count * 3);
        var publications = new List<DailyPublication>(dates.Count);

        foreach (var date in dates)
        {
            var byService = serviceAggregates
                .Where(pair => pair.Key.Date == date)
                .Select(pair => new CostByServiceRow
                {
                    Label = pair.Key.ServiceName,
                    Currency = pair.Key.Currency,
                    TotalCost = pair.Value.TotalCost,
                    Count = pair.Value.Count,
                    SubscriptionId = candidate.SubscriptionId
                })
                .OrderByDescending(row => row.TotalCost)
                .ToList();

            var byResource = resourceAggregates
                .Where(pair => pair.Key.Date == date)
                .Select(pair => new CostByResourceRow
                {
                    ResourceId = pair.Key.ResourceId,
                    Label = ExtractResourceName(pair.Key.ResourceId),
                    ServiceName = pair.Key.ServiceName,
                    ResourceGroup = pair.Key.ResourceGroup,
                    ResourceLocation = pair.Key.ResourceLocation,
                    ResourceType = pair.Key.ResourceType,
                    Tags = pair.Key.Tags,
                    CostCenter = CostTagParser.TryGetTagValue(pair.Key.Tags, _costAllocationTagName) ?? string.Empty,
                    Currency = pair.Key.Currency,
                    TotalCost = pair.Value.TotalCost,
                    Count = pair.Value.Count,
                    SubscriptionId = candidate.SubscriptionId
                })
                .OrderByDescending(row => row.TotalCost)
                .ToList();

            var allocation = dailyTotals
                .Where(pair => pair.Key.Date == date)
                .Select(pair =>
                {
                    dailyAllocatedTotals.TryGetValue(pair.Key, out var allocatedCost);
                    return new CostAllocationSummary
                    {
                        Source = CostDataSources.CostManagementExport,
                        SubscriptionId = candidate.SubscriptionId,
                        UsageDate = date,
                        Currency = pair.Key.Currency,
                        TotalCost = pair.Value,
                        AllocatedResourceCost = allocatedCost,
                        UnallocatedCost = pair.Value - allocatedCost
                    };
                })
                .OrderBy(item => item.Currency, StringComparer.OrdinalIgnoreCase)
                .ToList();

            publications.Add(new DailyPublication(date, byService, byResource, allocation));
            publishedBlobs.Add(BuildByServicePublicationPath(date, candidate.SubscriptionId, candidate.PublicationMode));
            publishedBlobs.Add(BuildByResourcePublicationPath(date, candidate.SubscriptionId, candidate.PublicationMode));
            publishedBlobs.Add(BuildAllocationPublicationPath(date, candidate.SubscriptionId, candidate.PublicationMode));
        }

        var processingCompletedAt = DateTimeOffset.UtcNow;
        var previousIndex = await _versions.LoadIndexAsync(
            candidate.SubscriptionId, candidate.PublicationMode, cancellationToken);
        var versioned = publications.Select(p => new CostExportDailySnapshot
        {
            SubscriptionId = candidate.SubscriptionId, PublicationMode = candidate.PublicationMode,
            Date = p.Date, ExportName = exportName, RunId = runId, CostBasis = "ActualCost",
            SubmittedTime = manifest.RunInfo.SubmittedTime, ImportedAt = processingCompletedAt,
            ByService = p.ByService.ToList(), ByResource = p.ByResource.ToList(), Allocation = p.Allocation.ToList()
        }).ToList();

        // OverwritePreviousReport makes the declared run window authoritative. A date that
        // existed in the previous index but disappeared from the replacement must not retain
        // stale cost. Empty snapshots are explicit tombstones and differ from an unversioned day.
        if (previousIndex != null
            && manifest.DeliveryConfig.DataOverwriteBehavior.Equals("OverwritePreviousReport", StringComparison.OrdinalIgnoreCase)
            && manifest.RunInfo.StartDate.HasValue
            && manifest.RunInfo.EndDate.HasValue)
        {
            var coverageStart = manifest.RunInfo.StartDate.Value.Date;
            var coverageEnd = manifest.RunInfo.EndDate.Value.Date;
            var published = versioned.Select(s => s.Date.Date).ToHashSet();
            foreach (var day in previousIndex.Days.Keys
                         .Select(value => DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture))
                         .Where(day => day >= coverageStart && day <= coverageEnd && !published.Contains(day)))
            {
                var previousReference = previousIndex.Days[day.ToString("yyyy-MM-dd")];
                var previousSnapshot = await _versions.LoadSnapshotAsync(
                    candidate.SubscriptionId, candidate.PublicationMode, previousReference.Revision, cancellationToken);
                var currencies = previousSnapshot.ByService.Select(row => row.Currency)
                    .Concat(previousSnapshot.Allocation.Select(row => row.Currency))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                versioned.Add(new CostExportDailySnapshot
                {
                    SubscriptionId = candidate.SubscriptionId, PublicationMode = candidate.PublicationMode,
                    Date = day, ExportName = exportName, RunId = runId, CostBasis = "ActualCost",
                    SubmittedTime = manifest.RunInfo.SubmittedTime, ImportedAt = processingCompletedAt,
                    IsTombstone = true,
                    TombstoneCurrency = currencies.Count == 1 ? currencies[0] : null
                });
            }
        }

        versioned = versioned.OrderBy(s => s.Date).ToList();
        var minUsageDate = versioned.Count == 0 ? (DateTime?)null : versioned[0].Date;
        var maxUsageDate = versioned.Count == 0 ? (DateTime?)null : versioned[^1].Date;

        var checkpoint = new CostExportImportCheckpoint
        {
            Source = CostDataSources.CostManagementExport,
            SubscriptionId = candidate.SubscriptionId,
            ExportName = exportName,
            RunId = runId,
            PublicationMode = candidate.PublicationMode,
            SourceManifest = candidate.ManifestName,
            SubmittedTime = manifest.RunInfo.SubmittedTime,
            MinUsageDate = minUsageDate,
            MaxUsageDate = maxUsageDate,
            ProcessingStartedAt = processingStartedAt,
            ProcessingCompletedAt = processingCompletedAt,
            PartitionsProcessed = partitionsProcessed,
            RowsProcessed = totalRows,
            PublishedDates = versioned.Count,
            TotalsByCurrency = totalsByCurrency,
            PublishedBlobs = publishedBlobs,
            Status = "Completed"
        };

        await using var publicationLease = await _costStorageRepository.TryAcquireExportPublicationLeaseAsync(
            candidate.SubscriptionId,
            exportName,
            candidate.PublicationMode,
            checkpoint.MaxUsageDate,
            manifest.RunInfo.SubmittedTime,
            runId,
            cancellationToken);
        if (publicationLease is null)
        {
            _logger.LogInformation(
                "Promoção ignorada porque um run mais novo já foi publicado: subscriptionId={subscriptionId} exportName={exportName} runId={runId} mode={mode} submittedTime={submittedTime}",
                candidate.SubscriptionId,
                exportName,
                runId,
                candidate.PublicationMode,
                manifest.RunInfo.SubmittedTime);
            return new ManifestOutcome(
                partitionsProcessed,
                totalRows,
                versioned.Count,
                checkpoint,
                Promoted: false,
                ControlDocumentsPromoted: false);
        }

        var promoteControlDocuments = await ShouldPromoteControlDocumentsAsync(
            checkpoint,
            cancellationToken);

        await _versions.PublishAsync(checkpoint, versioned, cancellationToken);
        var committed = await _versions.LoadIndexAsync(candidate.SubscriptionId, candidate.PublicationMode, cancellationToken);

        foreach (var publication in publications)
        {
            // Legacy mirrors may only follow the revision selected for this day, never overwrite a newer day.
            var snapshot = versioned.Single(s => s.Date == publication.Date);
            if (committed != null && (!committed.Days.TryGetValue(publication.Date.ToString("yyyy-MM-dd"), out var selected)
                || selected.Revision != snapshot.Revision)) continue;
            await _costStorageRepository.SaveExportByServiceAsync(
                publication.Date,
                candidate.SubscriptionId,
                publication.ByService,
                candidate.PublicationMode,
                cancellationToken);
            await _costStorageRepository.SaveExportByResourceAsync(
                publication.Date,
                candidate.SubscriptionId,
                publication.ByResource,
                candidate.PublicationMode,
                cancellationToken);
            await _costStorageRepository.SaveExportAllocationAsync(
                publication.Date,
                candidate.SubscriptionId,
                publication.Allocation,
                candidate.PublicationMode,
                cancellationToken);
            foreach (var summary in publication.Allocation)
            {
                _logger.LogInformation(
                    "Alocação de custo publicada: subscriptionId={subscriptionId} source={source} runId={runId} mode={mode} usageDate={usageDate} currency={currency} totalCost={totalCost} allocatedResourceCost={allocatedResourceCost} unallocatedCost={unallocatedCost}",
                    candidate.SubscriptionId,
                    CostDataSources.CostManagementExport,
                    runId,
                    candidate.PublicationMode,
                    publication.Date.ToString("yyyy-MM-dd"),
                    summary.Currency,
                    summary.TotalCost,
                    summary.AllocatedResourceCost,
                    summary.UnallocatedCost);
            }
        }

        foreach (var tombstone in versioned.Where(snapshot => snapshot.IsTombstone))
        {
            if (committed == null
                || !committed.Days.TryGetValue(tombstone.Date.ToString("yyyy-MM-dd"), out var selected)
                || selected.Revision != tombstone.Revision)
            {
                continue;
            }

            await _costStorageRepository.SaveExportByServiceAsync(
                tombstone.Date, candidate.SubscriptionId, [], candidate.PublicationMode, cancellationToken);
            await _costStorageRepository.SaveExportByResourceAsync(
                tombstone.Date, candidate.SubscriptionId, [], candidate.PublicationMode, cancellationToken);
            await _costStorageRepository.SaveExportAllocationAsync(
                tombstone.Date, candidate.SubscriptionId, [], candidate.PublicationMode, cancellationToken);
        }

        await _costStorageRepository.SaveImportRunCheckpointAsync(checkpoint, cancellationToken);
        if (promoteControlDocuments)
        {
            await _costStorageRepository.UpdateLatestImportCheckpointAsync(checkpoint, cancellationToken);
            await _costStorageRepository.SavePublicationWatermarkAsync(
                CreateWatermark(checkpoint),
                cancellationToken);
        }
        else
        {
            _logger.LogInformation(
                "Run histórico publicado sem regredir o watermark: subscriptionId={subscriptionId} exportName={exportName} runId={runId} maxUsageDate={maxUsageDate}",
                candidate.SubscriptionId,
                exportName,
                runId,
                maxUsageDate?.ToString("yyyy-MM-dd"));
        }

        _logger.LogInformation(
            "Manifesto processado: subscriptionId={subscriptionId} source={source} exportName={exportName} runId={runId} mode={mode} manifest={manifest} minUsageDate={minUsageDate} maxUsageDate={maxUsageDate} partitions={partitions} rows={rows} publishedBlobs={publishedBlobs} durationMs={durationMs}",
            candidate.SubscriptionId,
            CostDataSources.CostManagementExport,
            exportName,
            runId,
            candidate.PublicationMode,
            candidate.ManifestName,
            minUsageDate?.ToString("yyyy-MM-dd"),
            maxUsageDate?.ToString("yyyy-MM-dd"),
            partitionsProcessed,
            totalRows,
            publishedBlobs,
            stopwatch.ElapsedMilliseconds);

        return new ManifestOutcome(
            partitionsProcessed,
            totalRows,
            versioned.Count,
            checkpoint,
            Promoted: true,
            ControlDocumentsPromoted: promoteControlDocuments);
    }

    private async Task<bool> ShouldPromoteControlDocumentsAsync(
        CostExportImportCheckpoint candidate,
        CancellationToken cancellationToken)
    {
        var watermark = await _costStorageRepository.LoadPublicationWatermarkAsync(
            candidate.SubscriptionId,
            candidate.PublicationMode,
            cancellationToken);
        if (!IsCandidateAtLeastAsCurrent(
                candidate.MaxUsageDate,
                candidate.SubmittedTime,
                candidate.RunId,
                watermark?.MaxUsageDate,
                watermark?.SubmittedTime,
                watermark?.RunId))
        {
            return false;
        }

        var latestCheckpoint = await _costStorageRepository.LoadLatestCompletedImportCheckpointAsync(
            candidate.SubscriptionId,
            candidate.ExportName,
            candidate.PublicationMode,
            cancellationToken);
        return IsCandidateAtLeastAsCurrent(
            candidate.MaxUsageDate,
            candidate.SubmittedTime,
            candidate.RunId,
            latestCheckpoint?.MaxUsageDate,
            latestCheckpoint?.SubmittedTime,
            latestCheckpoint?.RunId);
    }

    private static bool IsCandidateAtLeastAsCurrent(
        DateTime? candidateMaxUsageDate,
        DateTimeOffset candidateSubmittedTime,
        string candidateRunId,
        DateTime? currentMaxUsageDate,
        DateTimeOffset? currentSubmittedTime,
        string? currentRunId)
    {
        if (!currentMaxUsageDate.HasValue)
        {
            return candidateMaxUsageDate.HasValue ||
                !currentSubmittedTime.HasValue ||
                CompareRuns(
                    candidateSubmittedTime,
                    candidateRunId,
                    currentSubmittedTime.Value,
                    currentRunId ?? string.Empty) >= 0;
        }

        if (!candidateMaxUsageDate.HasValue)
        {
            return false;
        }

        var usageDateComparison = DateTime.Compare(
            candidateMaxUsageDate.Value.Date,
            currentMaxUsageDate.Value.Date);
        return usageDateComparison > 0 ||
            (usageDateComparison == 0 &&
             (!currentSubmittedTime.HasValue ||
              CompareRuns(
                  candidateSubmittedTime,
                  candidateRunId,
                  currentSubmittedTime.Value,
                  currentRunId ?? string.Empty) >= 0));
    }

    private static int CompareRuns(
        DateTimeOffset leftSubmittedTime,
        string leftRunId,
        DateTimeOffset rightSubmittedTime,
        string rightRunId)
    {
        var submittedComparison = leftSubmittedTime.CompareTo(rightSubmittedTime);
        return submittedComparison != 0
            ? submittedComparison
            : string.Compare(leftRunId, rightRunId, StringComparison.Ordinal);
    }

    private HashSet<string> ParseAllowlist()
    {
        return (_configuration["COST_EXPORT_SUBSCRIPTIONS"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<HashSet<string>> ResolveIngestionSubscriptionsAsync(
        CancellationToken cancellationToken)
    {
        if (!CostCollectionRouting.IsImportAllExportSubscriptionsEnabled(_configuration))
        {
            return ParseAllowlist();
        }

        var subscriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var subscriptionId in _exportSource.ListSubscriptionIdsAsync(cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(subscriptionId))
            {
                subscriptions.Add(subscriptionId);
            }
        }

        _logger.LogInformation(
            "Descoberta de subscriptions no container de Exports concluída: subscriptions={subscriptions}",
            subscriptions.Count);
        return subscriptions;
    }

    internal static IReadOnlyList<string> RotateSubscriptions(
        IEnumerable<string> subscriptions,
        string? lastSubscriptionId)
    {
        var ordered = subscriptions
            .Where(subscription => !string.IsNullOrWhiteSpace(subscription))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(subscription => subscription, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ordered.Count < 2 || string.IsNullOrWhiteSpace(lastSubscriptionId))
        {
            return ordered;
        }

        var lastIndex = ordered.FindIndex(subscription =>
            subscription.Equals(lastSubscriptionId, StringComparison.OrdinalIgnoreCase));
        return lastIndex < 0
            ? ordered
            : ordered.Skip(lastIndex + 1).Concat(ordered.Take(lastIndex + 1)).ToList();
    }

    private int GetMaximumManifestsPerRun()
    {
        var configured = _configuration.GetValue("COST_EXPORT_MAX_MANIFESTS_PER_RUN", 100);
        if (configured <= 0)
        {
            throw new InvalidOperationException(
                "COST_EXPORT_MAX_MANIFESTS_PER_RUN deve ser maior que zero.");
        }

        return configured;
    }

    private static bool TryExtractCurrentSubscriptionId(
        string manifestName,
        out string subscriptionId)
    {
        subscriptionId = string.Empty;
        if (string.IsNullOrWhiteSpace(manifestName) ||
            manifestName.Contains('\\') ||
            manifestName.Split('/').Any(segment => segment.Equals("..", StringComparison.Ordinal)))
        {
            return false;
        }

        var segments = manifestName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 5 ||
            !segments[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(segments[1], out _) ||
            !segments[2].Equals("billing-period", StringComparison.OrdinalIgnoreCase) ||
            !segments[3].Equals("current", StringComparison.OrdinalIgnoreCase) ||
            !segments[^1].Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        subscriptionId = segments[1];
        return true;
    }

    private static bool IsManifestFromNewerBillingPeriod(
        string manifestName,
        string? currentManifestName,
        DateTime? currentMaxUsageDate)
    {
        var candidatePeriodStart = TryExtractBillingPeriodStart(manifestName);
        if (!candidatePeriodStart.HasValue)
        {
            return false;
        }

        if (currentMaxUsageDate.HasValue)
        {
            return candidatePeriodStart.Value.Date > currentMaxUsageDate.Value.Date;
        }

        var currentPeriodStart = TryExtractBillingPeriodStart(currentManifestName);
        return currentPeriodStart.HasValue &&
            candidatePeriodStart.Value.Date > currentPeriodStart.Value.Date;
    }

    private static DateTime? TryExtractBillingPeriodStart(string? manifestName)
    {
        if (string.IsNullOrWhiteSpace(manifestName))
        {
            return null;
        }

        foreach (var segment in manifestName.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length != 17 ||
                segment[8] != '-' ||
                !DateTime.TryParseExact(
                    segment.AsSpan(0, 8),
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var periodStart) ||
                !DateTime.TryParseExact(
                    segment.AsSpan(9, 8),
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var periodEnd) ||
                periodStart > periodEnd)
            {
                continue;
            }

            return periodStart.Date;
        }

        return null;
    }

    private static void ValidateManifestStructure(
        CostExportManifest manifest,
        string manifestName,
        string subscriptionId)
    {
        var exportName = manifest.ExportConfig.ExportName.Trim();
        if (!exportName.Equals(SupportedExportName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Export não suportado no manifesto '{manifestName}': '{exportName}'.");
        }

        if (manifest.RunInfo.RunId.Any(character =>
                !char.IsLetterOrDigit(character) && character != '-'))
        {
            throw new InvalidDataException(
                $"runInfo.runId contém caracteres inválidos: '{manifest.RunInfo.RunId}'.");
        }

        if (manifest.RunInfo.StartDate.HasValue != manifest.RunInfo.EndDate.HasValue
            || (manifest.RunInfo.StartDate.HasValue
                && manifest.RunInfo.EndDate!.Value.Date < manifest.RunInfo.StartDate.Value.Date))
        {
            throw new InvalidDataException(
                $"Janela runInfo inválida no manifesto '{manifestName}': startDate e endDate devem formar um intervalo válido.");
        }

        if (!manifest.ExportConfig.Granularity.Equals("Daily", StringComparison.OrdinalIgnoreCase) ||
            !manifest.DeliveryConfig.PartitionData ||
            !manifest.DeliveryConfig.DataOverwriteBehavior.Equals("OverwritePreviousReport", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Configuração de export não suportada no manifesto '{manifestName}'. Esperado Daily + partitionData + OverwritePreviousReport.");
        }

        var expectedRoot = $"subscriptions/{subscriptionId}/billing-period/current";
        if (!manifest.DeliveryConfig.RootFolderPath.TrimEnd('/')
                .Equals(expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"rootFolderPath do manifesto não corresponde à assinatura/path esperado: '{manifest.DeliveryConfig.RootFolderPath}'.");
        }

        if (!manifest.DeliveryConfig.FileFormat.Equals("Csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Formato de arquivo não suportado: '{manifest.DeliveryConfig.FileFormat}'.");
        }

        var compression = manifest.DeliveryConfig.CompressionMode;
        if (!compression.Equals("None", StringComparison.OrdinalIgnoreCase) &&
            !compression.Equals("gzip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Compressão não suportada: '{compression}'.");
        }

        if (manifest.BlobCount < 0 || manifest.DataRowCount < 0)
        {
            throw new InvalidDataException("blobCount e dataRowCount não podem ser negativos.");
        }

        foreach (var partition in manifest.Blobs)
        {
            ValidatePartitionPath(partition.BlobName, subscriptionId, exportName);
            if (partition.DataRowCount < 0)
            {
                throw new InvalidDataException(
                    $"dataRowCount negativo na partição '{partition.BlobName}'.");
            }
        }
    }

    private static void ValidateCurrentExportContract(
        CostExportManifest manifest,
        string manifestName)
    {
        if (!manifest.ExportConfig.Type.Equals("Usage", StringComparison.OrdinalIgnoreCase) ||
            !manifest.ExportConfig.TimeFrame.Equals("MonthToDate", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Definição de export não suportada no manifesto '{manifestName}'. Esperado Usage + MonthToDate.");
        }
    }

    private static void ValidatePartitionPath(
        string blobName,
        string subscriptionId,
        string exportName)
    {
        if (string.IsNullOrWhiteSpace(blobName) ||
            blobName.StartsWith("/", StringComparison.Ordinal) ||
            blobName.Contains('\\') ||
            Uri.TryCreate(blobName, UriKind.Absolute, out _) ||
            blobName.Contains('?') ||
            blobName.Contains('#'))
        {
            throw new InvalidDataException($"blobName inválido no manifesto: '{blobName}'.");
        }

        var segments = blobName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment.Equals("..", StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"blobName contém segmento proibido '..': '{blobName}'.");
        }

        var expectedPrefix =
            $"subscriptions/{subscriptionId}/billing-period/current/{exportName}/";
        if (!blobName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"blobName não pertence à assinatura, período ou export do manifesto: '{blobName}'.");
        }

        if (!blobName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
            !blobName.EndsWith(".csv.gz", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Extensão de partição não suportada: '{blobName}'.");
        }
    }

    private static Stream CreateDataStream(
        Stream source,
        string compressionMode,
        string blobName)
    {
        var isGzip = compressionMode.Equals("gzip", StringComparison.OrdinalIgnoreCase) ||
                     blobName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        return isGzip
            ? new GZipStream(source, CompressionMode.Decompress, leaveOpen: false)
            : source;
    }

    private static void ValidateManifestTotals(
        CostExportManifest manifest,
        int partitionsProcessed,
        long rowsProcessed)
    {
        if (partitionsProcessed != manifest.BlobCount ||
            manifest.Blobs.Count != manifest.BlobCount)
        {
            throw new InvalidDataException(
                $"blobCount inconsistente: manifesto={manifest.BlobCount}, lista={manifest.Blobs.Count}, processadas={partitionsProcessed}.");
        }

        var declaredRows = manifest.Blobs.Sum(partition => partition.DataRowCount);
        if (declaredRows != manifest.DataRowCount)
        {
            throw new InvalidDataException(
                $"dataRowCount inconsistente: manifesto={manifest.DataRowCount}, soma das partições={declaredRows}.");
        }

        if (rowsProcessed != manifest.DataRowCount)
        {
            throw new InvalidDataException(
                $"dataRowCount inconsistente: manifesto={manifest.DataRowCount}, linhas válidas={rowsProcessed}.");
        }
    }

    private static void Add<TKey>(
        IDictionary<TKey, AggregateValue> aggregates,
        TKey key,
        decimal cost)
        where TKey : notnull
    {
        if (!aggregates.TryGetValue(key, out var aggregate))
        {
            aggregate = new AggregateValue();
            aggregates[key] = aggregate;
        }

        aggregate.TotalCost += cost;
        aggregate.Count++;
    }

    private static void AddCurrencyTotal(
        IDictionary<string, decimal> totalsByCurrency,
        string currency,
        decimal cost)
    {
        var normalizedCurrency = string.IsNullOrWhiteSpace(currency) ? "BRL" : currency.Trim();
        totalsByCurrency.TryGetValue(normalizedCurrency, out var currentTotal);
        totalsByCurrency[normalizedCurrency] = currentTotal + cost;
    }

    private static void AddCost<TKey>(IDictionary<TKey, decimal> totals, TKey key, decimal cost)
        where TKey : notnull
    {
        totals.TryGetValue(key, out var current);
        totals[key] = current + cost;
    }

    private static string BuildByServicePublicationPath(
        DateTime date,
        string subscriptionId,
        string publicationMode)
    {
        var prefix = publicationMode.Equals(CostExportPublicationModes.Shadow, StringComparison.OrdinalIgnoreCase)
            ? "cost/shadow/byService"
            : "cost/byService";
        return $"{prefix}/date={date:yyyy-MM-dd}/subscriptionId={subscriptionId}/byService.json";
    }

    private static string BuildByResourcePublicationPath(
        DateTime date,
        string subscriptionId,
        string publicationMode)
    {
        var prefix = publicationMode.Equals(CostExportPublicationModes.Shadow, StringComparison.OrdinalIgnoreCase)
            ? "cost/shadow/byResource"
            : "cost/byResource";
        return $"{prefix}/date={date:yyyy-MM-dd}/subscriptionId={subscriptionId}/byResource.json";
    }

    private static string BuildAllocationPublicationPath(
        DateTime date,
        string subscriptionId,
        string publicationMode)
    {
        var prefix = publicationMode.Equals(CostExportPublicationModes.Shadow, StringComparison.OrdinalIgnoreCase)
            ? "cost/shadow/allocation"
            : "cost/allocation";
        return $"{prefix}/date={date:yyyy-MM-dd}/subscriptionId={subscriptionId}/allocation.json";
    }

    private static CostExportPublicationWatermark CreateWatermark(
        CostExportImportCheckpoint checkpoint)
    {
        return new CostExportPublicationWatermark
        {
            Source = checkpoint.Source,
            SubscriptionId = checkpoint.SubscriptionId,
            ExportName = checkpoint.ExportName,
            RunId = checkpoint.RunId,
            PublicationMode = checkpoint.PublicationMode,
            SubmittedTime = checkpoint.SubmittedTime,
            MinUsageDate = checkpoint.MinUsageDate,
            MaxUsageDate = checkpoint.MaxUsageDate,
            ProcessedAt = checkpoint.ProcessingCompletedAt,
            PartitionsProcessed = checkpoint.PartitionsProcessed,
            RowsProcessed = checkpoint.RowsProcessed,
            TotalsByCurrency = checkpoint.TotalsByCurrency,
            PublishedBlobs = checkpoint.PublishedBlobs,
            Status = checkpoint.Status
        };
    }

    private static string ExtractResourceName(string resourceId)
    {
        var segments = resourceId.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? resourceId : segments[^1];
    }

    private static string BuildFailure(
        string subscriptionId,
        string? exportName,
        string? runId,
        string manifestName,
        string reason)
    {
        return $"subscriptionId={subscriptionId} exportName={exportName ?? "unknown"} runId={runId ?? "unknown"} manifest={manifestName} reason={reason}";
    }

    private sealed record ManifestCandidate(
        string ManifestName,
        string SubscriptionId,
        CostExportManifest Manifest,
        string PublicationMode);

    private sealed record ManifestOutcome(
        int PartitionsProcessed,
        long RowsProcessed,
        int PublishedDates,
        CostExportImportCheckpoint Checkpoint,
        bool Promoted,
        bool ControlDocumentsPromoted);

    private sealed record DailyPublication(
        DateTime Date,
        IReadOnlyCollection<CostByServiceRow> ByService,
        IReadOnlyCollection<CostByResourceRow> ByResource,
        IReadOnlyCollection<CostAllocationSummary> Allocation);

    private sealed record ServiceAggregateKey(
        DateTime Date,
        string ServiceName,
        string Currency);

    private sealed record ResourceAggregateKey(
        DateTime Date,
        string ResourceId,
        string ServiceName,
        string Currency,
        string ResourceGroup,
        string ResourceLocation,
        string ResourceType,
        string Tags);

    private sealed record DailyCurrencyKey(DateTime Date, string Currency);

    private sealed class AggregateValue
    {
        public decimal TotalCost { get; set; }
        public int Count { get; set; }
    }
}
