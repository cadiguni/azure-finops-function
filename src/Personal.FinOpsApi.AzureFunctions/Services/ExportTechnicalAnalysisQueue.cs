using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Bridges a promoted cost Export to the existing subscription-analysis worker.
/// Keeping this behind an interface makes the import path independently testable.
/// </summary>
public interface IExportTechnicalAnalysisQueue
{
    Task EnqueueAsync(CostExportAnalysisTarget target, CancellationToken cancellationToken = default);
}

public sealed class ExportTechnicalAnalysisQueue : IExportTechnicalAnalysisQueue
{
    private readonly QueueService _queueService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExportTechnicalAnalysisQueue> _logger;

    public ExportTechnicalAnalysisQueue(
        QueueService queueService,
        IConfiguration configuration,
        ILogger<ExportTechnicalAnalysisQueue> logger)
    {
        _queueService = queueService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task EnqueueAsync(CostExportAnalysisTarget target, CancellationToken cancellationToken = default)
    {
        if (!_configuration.GetValue("EnableCostExportTechnicalAnalysis", true))
        {
            _logger.LogInformation("Análise técnica após Export desabilitada.");
            return;
        }

        // The import checkpoint guarantees this is called only for a promoted run.
        // The worker persists its result; no portal request reaches Azure Monitor.
        var queued = await _queueService.SendSubscriptionAnalysisAsync(
            target.SubscriptionId,
            "export-refresh");
        if (!queued)
        {
            throw new InvalidOperationException(
                $"Não foi possível enfileirar análise técnica para {target.SubscriptionId}, run {target.RunId}.");
        }

        _logger.LogInformation(
            "Análise técnica enfileirada após Export: subscriptionId={subscriptionId} runId={runId}",
            target.SubscriptionId,
            target.RunId);
    }
}
