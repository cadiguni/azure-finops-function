using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public sealed class ExportFirstCostDataReader : ICostDataReader
{
    private const int DefaultMaximumStalenessDays = 3;
    private static readonly string[] ResourceDimensions =
        ["ResourceId", "ResourceType", "ResourceGroupName", "ServiceName", "MeterCategory", "MeterSubCategory"];
    private readonly ICostStorageRepository _repository;
    private readonly ICostExportVersionStore _versions;
    private readonly ICostManagementClient _costManagementClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExportFirstCostDataReader> _logger;
    private int MaximumStalenessDays => _configuration.GetValue("COST_EXPORT_ANOMALY_MAX_STALENESS_DAYS", DefaultMaximumStalenessDays);

    public ExportFirstCostDataReader(
        ICostStorageRepository repository,
        ICostExportVersionStore versions,
        ICostManagementClient costManagementClient,
        IConfiguration configuration,
        ILogger<ExportFirstCostDataReader> logger)
    {
        _repository = repository;
        _versions = versions;
        _costManagementClient = costManagementClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<DailyCostReadResult> ReadDailyCostsAsync(
        string subscriptionId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default)
    {
        var normalizedStart = startDate.Date;
        var normalizedEnd = endDate.Date;
        if (normalizedEnd < normalizedStart)
        {
            throw new ArgumentException("A data final deve ser igual ou posterior à data inicial.");
        }

        if (!CostCollectionRouting.IsExportIngestionSubscription(_configuration, subscriptionId))
        {
            return await ReadQueryApiAsync(
                subscriptionId,
                normalizedStart,
                normalizedEnd,
                usedFallback: false,
                fallbackReason: null,
                cancellationToken);
        }

        try
        {
            return await ReadExportAsync(
                subscriptionId,
                normalizedStart,
                normalizedEnd,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var reason = exception.Message;
            _logger.LogWarning(
                exception,
                "[COST-DATA] Export indisponível; usando Query API temporariamente: subscriptionId={subscriptionId} startDate={startDate} endDate={endDate} reason={reason}",
                subscriptionId,
                normalizedStart.ToString("yyyy-MM-dd"),
                normalizedEnd.ToString("yyyy-MM-dd"),
                reason);
            return await ReadQueryApiAsync(
                subscriptionId,
                normalizedStart,
                normalizedEnd,
                usedFallback: true,
                fallbackReason: reason,
                cancellationToken);
        }
    }

    public async Task<ResourceCostReadResult> ReadResourceCostsAsync(
        string subscriptionId, DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
    {
        startDate = startDate.Date;
        endDate = endDate.Date;
        if (endDate < startDate) throw new ArgumentException("A data final deve ser igual ou posterior à data inicial.");
        cancellationToken.ThrowIfCancellationRequested();
        string? fallbackReason = null;
        if (CostCollectionRouting.IsExportIngestionSubscription(_configuration, subscriptionId))
        {
            try
            {
                var window = await GetExportWindowAsync(subscriptionId, startDate, endDate, cancellationToken);
                var rows = new List<CostDiagnosticRow>();
                var legacyDates = new HashSet<string>(StringComparer.Ordinal);
                var availableDates = new HashSet<DateTime>();
                for (var date = startDate; date <= window.EffectiveEnd; date = date.AddDays(1))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var snapshot = await ReadVersionAsync(window, subscriptionId, date, cancellationToken);
                    var dailyRows = snapshot != null
                        ? snapshot.ByResource
                        : await _repository.LoadExportByResourceAsync(date, subscriptionId, window.PublicationMode, cancellationToken);
                    if (window.Index != null && snapshot == null && dailyRows.Count > 0)
                        legacyDates.Add(date.ToString("yyyy-MM-dd"));
                    if (snapshot != null || dailyRows.Count > 0)
                        availableDates.Add(date);
                    if (dailyRows.Any(r => !string.IsNullOrWhiteSpace(r.SubscriptionId)
                        && !r.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Snapshot de recurso contém outra subscription.");
                    rows.AddRange(dailyRows.Select(r => new CostDiagnosticRow
                    {
                        ResourceId = r.ResourceId, ResourceType = r.ResourceType,
                        ResourceGroupName = r.ResourceGroup, ServiceName = r.ServiceName,
                        TotalCost = r.TotalCost, Currency = NormalizeCurrency(r.Currency), UsageDate = date
                    }));
                }
                if (availableDates.Count == 0)
                    throw new InvalidDataException("O Export não retornou snapshots por recurso na janela solicitada.");
                var dataAsOf = availableDates.Max();
                if ((endDate - dataAsOf).TotalDays > MaximumStalenessDays)
                    throw new InvalidDataException("Snapshots por recurso estão defasados mesmo com watermark recente.");
                EnsureSingleCurrency(rows);
                return new ResourceCostReadResult
                {
                    Rows = rows,
                    Provenance = new CostReadProvenance
                    {
                        Source = CostDataSources.CostManagementExport,
                        DatasetRevision = BuildDatasetRevision(window, legacyDates),
                        DailyRevisions = GetDailyRevisions(window, startDate, endDate, legacyDates),
                        DataAsOf = dataAsOf,
                        ImportCompletedAt = window.Watermark.ProcessedAt,
                        ExportRunId = window.Watermark.RunId,
                        PublicationMode = window.PublicationMode,
                        RequestedFrom = startDate, RequestedTo = endDate,
                        AvailableDates = availableDates.OrderBy(date => date).ToList()
                    }
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                fallbackReason = exception.Message;
                _logger.LogWarning(exception,
                    "[COST-DATA] Export por recurso indisponível; usando Query API: subscriptionId={subscriptionId}", subscriptionId);
            }
        }

        var queryRows = await _costManagementClient.QueryCostDiagnosticsAsync(
            subscriptionId, startDate, endDate, "Daily", ResourceDimensions, cancellationToken);
        var filteredRows = queryRows.Where(r => r.UsageDate.HasValue
            && r.UsageDate.Value.Date >= startDate && r.UsageDate.Value.Date <= endDate).ToList();
        EnsureSingleCurrency(filteredRows);
        return new ResourceCostReadResult
        {
            Rows = filteredRows,
            Provenance = new CostReadProvenance
            {
                Source = CostDataSources.CostManagementQueryApi,
                DataAsOf = filteredRows.Select(r => r.UsageDate).DefaultIfEmpty().Max(),
                UsedFallback = fallbackReason != null, FallbackReason = fallbackReason,
                RequestedFrom = startDate, RequestedTo = endDate,
                AvailableDates = filteredRows.Select(r => r.UsageDate!.Value.Date).Distinct().OrderBy(d => d).ToList()
            }
        };
    }

    private static void EnsureSingleCurrency(IEnumerable<CostDiagnosticRow> rows)
    {
        if (rows.Select(r => NormalizeCurrency(r.Currency)).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            throw new InvalidDataException("A leitura por recurso não pode somar moedas diferentes.");
    }

    private async Task<DailyCostReadResult> ReadExportAsync(
        string subscriptionId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken)
    {
        var window = await GetExportWindowAsync(subscriptionId, startDate, endDate, cancellationToken);
        var publicationMode = window.PublicationMode;
        var maxUsageDate = window.Watermark.MaxUsageDate!.Value.Date;
        var effectiveEnd = window.EffectiveEnd;

        var rowsByDate = new List<(DateTime Date, CostByServiceRow Row)>();
        var legacyDates = new HashSet<string>(StringComparer.Ordinal);
        for (var date = startDate; date <= effectiveEnd; date = date.AddDays(1))
        {
            var snapshot = await ReadVersionAsync(window, subscriptionId, date, cancellationToken);
            var rows = snapshot != null
                ? snapshot.IsTombstone
                    ? [new CostByServiceRow
                    {
                        SubscriptionId = subscriptionId,
                        Currency = snapshot.TombstoneCurrency
                            ?? throw new InvalidDataException("Tombstone sem moeda do custo anterior."),
                        TotalCost = 0m
                    }]
                    : snapshot.ByService
                : await _repository.LoadExportByServiceAsync(date, subscriptionId, publicationMode, cancellationToken);
            if (window.Index != null && snapshot == null && rows.Count > 0)
                legacyDates.Add(date.ToString("yyyy-MM-dd"));
            rowsByDate.AddRange(rows.Select(row => (date, row)));
        }

        var entries = BuildEntries(
            rowsByDate.Select(item => new DailyCostValue(
                item.Date,
                item.Row.TotalCost,
                item.Row.Currency)),
            CostDataSources.CostManagementExport);
        if (entries.Count == 0)
        {
            throw new InvalidDataException("O Export não retornou custos na janela solicitada.");
        }

        foreach (var entry in entries)
        {
            if (window.Index?.Days.TryGetValue(entry.Date, out var day) == true)
            {
                entry.Revision = day.Revision;
                entry.CostBasis = day.CostBasis;
            }
            else if (legacyDates.Contains(entry.Date))
            {
                entry.Revision = "legacy-unversioned";
                entry.CostBasis = "ActualCost";
            }
        }

        _logger.LogInformation(
            "[COST-DATA] Custos diários lidos do Export: subscriptionId={subscriptionId} mode={mode} startDate={startDate} endDate={endDate} dataAsOf={dataAsOf} days={days}",
            subscriptionId,
            publicationMode,
            startDate.ToString("yyyy-MM-dd"),
            effectiveEnd.ToString("yyyy-MM-dd"),
            maxUsageDate.ToString("yyyy-MM-dd"),
            entries.Count);
        return new DailyCostReadResult
        {
            Source = CostDataSources.CostManagementExport,
            DataAsOf = maxUsageDate,
            Entries = entries
        };
    }

    private async Task<ExportWindow> GetExportWindowAsync(
        string subscriptionId, DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
    {
        var publicationMode = CostCollectionRouting.IsCutoverSubscription(_configuration, subscriptionId)
            ? CostExportPublicationModes.Cutover
            : CostExportPublicationModes.Shadow;
        var index = await _versions.LoadIndexAsync(subscriptionId, publicationMode, cancellationToken);
        var persistedWatermark = await _repository.LoadPublicationWatermarkAsync(
            subscriptionId, publicationMode, cancellationToken);
        CostExportPublicationWatermark? watermark = persistedWatermark;
        if (index?.Days.Count > 0)
        {
            var indexMin = DateTime.ParseExact(index.Days.First().Key, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var indexMax = DateTime.ParseExact(index.Days.Last().Key, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var persistedIsComplete = persistedWatermark != null
                && persistedWatermark.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                && persistedWatermark.MinUsageDate.HasValue
                && persistedWatermark.MaxUsageDate.HasValue;
            var persistedIsNewer = persistedIsComplete && persistedWatermark!.MaxUsageDate!.Value.Date > indexMax;
            watermark = new CostExportPublicationWatermark
            {
                SubscriptionId = subscriptionId, PublicationMode = publicationMode,
                MinUsageDate = persistedIsComplete
                    ? new[] { indexMin, persistedWatermark!.MinUsageDate!.Value.Date }.Min()
                    : indexMin,
                MaxUsageDate = persistedIsComplete
                    ? new[] { indexMax, persistedWatermark!.MaxUsageDate!.Value.Date }.Max()
                    : indexMax,
                RunId = persistedIsNewer ? persistedWatermark!.RunId : index.Days.Last().Value.RunId,
                ProcessedAt = persistedIsComplete
                    ? new[] { index.Days.Values.Max(day => day.ImportedAt), persistedWatermark!.ProcessedAt }.Max()
                    : index.Days.Values.Max(day => day.ImportedAt)
            };
        }
        if (watermark is null ||
            !watermark.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
            !watermark.MinUsageDate.HasValue ||
            !watermark.MaxUsageDate.HasValue)
        {
            throw new InvalidDataException(
                $"Watermark completo não encontrado para mode={publicationMode}.");
        }

        var minUsageDate = watermark.MinUsageDate.Value.Date;
        var maxUsageDate = watermark.MaxUsageDate.Value.Date;

        var maximumStalenessDays = MaximumStalenessDays;
        if (maximumStalenessDays < 0)
        {
            throw new InvalidOperationException(
                "COST_EXPORT_ANOMALY_MAX_STALENESS_DAYS não pode ser negativo.");
        }

        if ((endDate - maxUsageDate).TotalDays > maximumStalenessDays)
        {
            throw new InvalidDataException(
                $"Watermark do Export está defasado: maxUsageDate={maxUsageDate:yyyy-MM-dd}, endDate={endDate:yyyy-MM-dd}.");
        }

        var effectiveEnd = maxUsageDate < endDate ? maxUsageDate : endDate;
        if (startDate > effectiveEnd)
        {
            throw new InvalidDataException(
                $"O Export não possui datas sobrepostas à janela solicitada: availableThrough={maxUsageDate:yyyy-MM-dd}, requested={startDate:yyyy-MM-dd}..{endDate:yyyy-MM-dd}.");
        }

        if (minUsageDate > startDate)
        {
            _logger.LogWarning(
                "[COST-DATA] A janela solicitada começa antes do manifest mais recente; snapshots históricos persistidos também serão lidos: subscriptionId={subscriptionId} mode={mode} requestedStart={requestedStart} manifestStart={manifestStart} endDate={endDate}.",
                subscriptionId,
                publicationMode,
                startDate.ToString("yyyy-MM-dd"),
                minUsageDate.ToString("yyyy-MM-dd"),
                effectiveEnd.ToString("yyyy-MM-dd"));
        }

        return new ExportWindow(publicationMode, watermark, effectiveEnd, index);
    }

    private sealed record ExportWindow(string PublicationMode, CostExportPublicationWatermark Watermark, DateTime EffectiveEnd, CostExportRevisionIndex? Index);

    private async Task<CostExportDailySnapshot?> ReadVersionAsync(ExportWindow window, string subscriptionId, DateTime date, CancellationToken cancellationToken)
    {
        if (window.Index == null || !window.Index.Days.TryGetValue(date.ToString("yyyy-MM-dd"), out var reference)) return null;
        var snapshot = await _versions.LoadSnapshotAsync(subscriptionId, window.PublicationMode, reference.Revision, cancellationToken);
        if (snapshot.Date.Date != date.Date || snapshot.CostBasis != "ActualCost")
            throw new InvalidDataException("Período ou conceito de custo incompatível no snapshot.");
        return snapshot;
    }

    private static string? BuildDatasetRevision(ExportWindow window, IReadOnlyCollection<string> legacyDates)
        => window.Index == null ? null : legacyDates.Count == 0
            ? window.Index.Revision
            : $"{window.Index.Revision}+legacy";

    private static IReadOnlyDictionary<string, string> GetDailyRevisions(
        ExportWindow window, DateTime startDate, DateTime endDate, IReadOnlyCollection<string> legacyDates)
    {
        var revisions = window.Index?.Days.Where(d => string.CompareOrdinal(d.Key, startDate.ToString("yyyy-MM-dd")) >= 0
                && string.CompareOrdinal(d.Key, endDate.ToString("yyyy-MM-dd")) <= 0)
            .ToDictionary(d => d.Key, d => d.Value.Revision) ?? new Dictionary<string, string>();
        foreach (var date in legacyDates) revisions[date] = "legacy-unversioned";
        return revisions;
    }

    private async Task<DailyCostReadResult> ReadQueryApiAsync(
        string subscriptionId,
        DateTime startDate,
        DateTime endDate,
        bool usedFallback,
        string? fallbackReason,
        CancellationToken cancellationToken)
    {
        var response = await _costManagementClient.QueryCostByServiceAsync(
            subscriptionId,
            startDate,
            endDate,
            granularity: "Daily",
            cancellationToken: cancellationToken);
        var entries = BuildEntries(
            response.Rows
                .Where(row => row.UsageDate.HasValue)
                .Select(row => new DailyCostValue(
                    row.UsageDate!.Value.Date,
                    row.TotalCost,
                    row.Currency)),
            CostDataSources.CostManagementQueryApi);
        foreach (var entry in entries)
        {
            entry.UsedFallback = usedFallback;
            entry.FallbackReason = fallbackReason;
        }
        var dataAsOf = entries
            .Select(entry => DateTime.ParseExact(
                entry.Date,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture))
            .Cast<DateTime?>()
            .Max();
        _logger.LogInformation(
            "[COST-DATA] Custos diários lidos da Query API: subscriptionId={subscriptionId} startDate={startDate} endDate={endDate} days={days} fallback={fallback}",
            subscriptionId,
            startDate.ToString("yyyy-MM-dd"),
            endDate.ToString("yyyy-MM-dd"),
            entries.Count,
            usedFallback);
        return new DailyCostReadResult
        {
            Source = CostDataSources.CostManagementQueryApi,
            DataAsOf = dataAsOf,
            UsedFallback = usedFallback,
            FallbackReason = fallbackReason,
            Entries = entries
        };
    }

    private static List<DailyCostEntry> BuildEntries(
        IEnumerable<DailyCostValue> values,
        string source)
    {
        var normalized = values
            .Select(value => value with { Currency = NormalizeCurrency(value.Currency) })
            .ToList();
        var currencies = normalized
            .Select(value => value.Currency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (currencies.Count > 1)
        {
            throw new InvalidDataException(
                $"A análise de anomalias não pode somar moedas diferentes: {string.Join(",", currencies)}.");
        }

        return normalized
            .GroupBy(value => value.Date.Date)
            .Select(group => new DailyCostEntry
            {
                Date = group.Key.ToString("yyyy-MM-dd"),
                TotalCost = group.Sum(value => value.TotalCost),
                Currency = currencies.FirstOrDefault() ?? "BRL",
                Source = source
            })
            .OrderBy(entry => entry.Date, StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeCurrency(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "BRL" : currency.Trim().ToUpperInvariant();

    private sealed record DailyCostValue(DateTime Date, decimal TotalCost, string Currency);
}
