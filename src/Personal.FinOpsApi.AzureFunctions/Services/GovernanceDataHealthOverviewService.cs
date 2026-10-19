using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Data health for every known subscription, without scanning the tenant: the subscription list comes
/// from the latest cost anomaly report (fallback: the export ingestion allowlist). Each row reuses
/// <see cref="GovernanceDataHealthService"/>, i.e. one index and one watermark read per subscription.
/// </summary>
public sealed class GovernanceDataHealthOverviewService(
    GovernanceDataHealthService health,
    ICostAnomalyReportRepository anomalyReports,
    IConfiguration configuration,
    ILogger<GovernanceDataHealthOverviewService> logger)
{
    internal const int MaxSubscriptions = 300;
    private const int MaxParallelReads = 8;
    private const int WindowDays = 14;

    public async Task<DataHealthOverviewResponse> GetAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var to = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-health.DelayDays);
        var from = to.AddDays(-(WindowDays - 1));
        var warnings = new List<string>();
        var (source, subscriptions) = await ResolveSubscriptionsAsync(warnings, cancellationToken);
        if (subscriptions.Count > MaxSubscriptions)
            throw new InvalidOperationException($"Mais de {MaxSubscriptions} subscriptions; visão consolidada não suportada.");

        var rows = new DataHealthOverviewRow[subscriptions.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, subscriptions.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelReads, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var (subscriptionId, name) = subscriptions[index];
                rows[index] = await ReadRowAsync(subscriptionId, name, from, to, now, token);
            });

        return new(from, to, now, source, rows
            .OrderBy(row => StatusOrder(row.Status))
            .ThenBy(row => row.SubscriptionName ?? row.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .ToList(), warnings);
    }

    private async Task<DataHealthOverviewRow> ReadRowAsync(string subscriptionId, string? name,
        DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ingestion = CostCollectionRouting.IsExportIngestionSubscription(configuration, subscriptionId);
        try
        {
            var result = await health.GetAsync(subscriptionId, from, to, now, cancellationToken);
            return new(result.SubscriptionId, name, ingestion, result.PublicationMode, result.Status,
                result.CoveragePercent, result.MissingDates?.Count, result.LatestUsageDate, result.IsStale,
                result.LastImportedAt, result.LastPublicationStatus);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Falha ao avaliar saúde dos dados de {SubscriptionId}", subscriptionId);
            return new(subscriptionId, name, ingestion, health.PublicationMode(subscriptionId), "unavailable",
                null, null, null, null, null, null);
        }
    }

    private async Task<(string Source, List<(string Id, string? Name)> Subscriptions)> ResolveSubscriptionsAsync(
        List<string> warnings, CancellationToken cancellationToken)
    {
        CostAnomalyReport? report = null;
        try { report = await anomalyReports.LoadLatestReportAsync(cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Relatório de anomalias indisponível para a visão de saúde dos dados");
        }

        var fromReport = (report?.Subscriptions ?? [])
            .Where(s => Guid.TryParse(s.SubscriptionId, out _))
            .GroupBy(s => Guid.Parse(s.SubscriptionId).ToString("D"))
            .Select(g => (g.Key, Name: g.Select(s => s.SubscriptionName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))))
            .ToList();
        if (fromReport.Count > 0)
            return ($"cost-anomaly-report:{(string.IsNullOrWhiteSpace(report!.RequestedDate) ? report.Date : report.RequestedDate)}",
                fromReport.Select(s => (s.Key, s.Name)).ToList());

        var allowlist = CostCollectionRouting.ParseAllowlist(configuration, CostCollectionRouting.ExportSubscriptionsSetting)
            .Where(id => Guid.TryParse(id, out _))
            .Select(id => (Guid.Parse(id).ToString("D"), (string?)null))
            .Distinct().ToList();
        warnings.Add(allowlist.Count > 0
            ? "Relatório de anomalias indisponível: lista de subscriptions vem de COST_EXPORT_SUBSCRIPTIONS, sem nomes."
            : "Nenhuma fonte de subscriptions disponível (relatório de anomalias ou COST_EXPORT_SUBSCRIPTIONS).");
        return (CostCollectionRouting.ExportSubscriptionsSetting, allowlist);
    }

    // Problems first so the admin sees what needs attention.
    private static int StatusOrder(string status) => status switch
    {
        "unavailable" => 0, "degraded" => 1, "incomplete" => 2, "unknown" => 3, "covered" => 4, _ => 5
    };
}
