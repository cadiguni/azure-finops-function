using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class CostExportImportTimerFunction
{
    private readonly ICostExportImportService _importService;
    private readonly ICostAnomalyQueueService _anomalyQueueService;
    private readonly IExportTechnicalAnalysisQueue _technicalAnalysisQueue;
    private readonly ICostAnomalyReportRepository _anomalyReportRepository;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostExportImportTimerFunction> _logger;

    public CostExportImportTimerFunction(
        ICostExportImportService importService,
        ICostAnomalyQueueService anomalyQueueService,
        IExportTechnicalAnalysisQueue technicalAnalysisQueue,
        ICostAnomalyReportRepository anomalyReportRepository,
        IConfiguration configuration,
        ILogger<CostExportImportTimerFunction> logger)
    {
        _importService = importService;
        _anomalyQueueService = anomalyQueueService;
        _technicalAnalysisQueue = technicalAnalysisQueue;
        _anomalyReportRepository = anomalyReportRepository;
        _configuration = configuration;
        _logger = logger;
    }

    [Function("CostExportImportTimer")]
    public async Task RunAsync(
        [TimerTrigger("%CostExportImportSchedule%")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("EnableCostExportImport", false))
        {
            _logger.LogInformation("Importação de Cost Management Exports está desabilitada.");
            return;
        }

        CostExportImportResult result;
        try
        {
            result = await _importService.ImportLatestAsync(cancellationToken);
        }
        catch (CostExportImportException exception)
        {
            await ProcessResultAsync(exception.Result, cancellationToken);
            throw;
        }

        await ProcessResultAsync(result, cancellationToken);
    }

    private async Task ProcessResultAsync(
        CostExportImportResult result,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Importação de exports concluída: manifestsFound={manifestsFound}, manifestsProcessed={manifestsProcessed}, manifestsSkipped={manifestsSkipped}, subscriptions={subscriptions}, partitions={partitions}, rows={rows}, dates={dates}, failures={failures}",
            result.ManifestsFound,
            result.ManifestsProcessed,
            result.ManifestsSkippedByCheckpoint,
            result.SubscriptionsProcessed,
            result.PartitionsProcessed,
            result.ImportedRows,
            result.PublishedDates,
            result.Failures.Count);

        if (!string.Equals(
            _configuration["EnableCostAnomalyAnalysis"],
            "false",
            StringComparison.OrdinalIgnoreCase))
        {
            var trigger = "cost-export-import";
            var shouldEnqueue = result.ManifestsProcessed > 0;
            if (!shouldEnqueue &&
                result.ManifestsSkippedByCheckpoint > 0 &&
                result.Failures.Count == 0 &&
                _configuration.GetValue("EnableCostAnomalyImportRecovery", true))
            {
                var latestReport = await _anomalyReportRepository.LoadLatestReportAsync(cancellationToken);
                var latestReportDate = latestReport is null
                    ? null
                    : DateTime.TryParse(latestReport.Date, out var parsedDate)
                        ? parsedDate.Date
                        : (DateTime?)null;
                shouldEnqueue = latestReportDate != DateTime.UtcNow.Date;
                trigger = "cost-export-import-recovery";
            }

            if (shouldEnqueue)
            {
                var runId = await _anomalyQueueService.EnqueueRunAsync(trigger, cancellationToken);
                _logger.LogInformation(
                    "Análise de anomalias enfileirada após importação: runId={runId} trigger={trigger} manifestsProcessed={manifestsProcessed}",
                    runId, trigger, result.ManifestsProcessed);
            }
        }

        foreach (var target in result.AnalysisTargets)
        {
            await _technicalAnalysisQueue.EnqueueAsync(target, cancellationToken);
        }
    }
}
