using Personal.FinOpsApi.AzureFunctions.Application;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class CostAnalysisTimerFunction
{
    private readonly CostAnalysisOrchestrator _orchestrator;
    private readonly SubscriptionDiscoveryService _subscriptionDiscovery;
    private readonly QueueService _queueService;
    private readonly ObservabilityService _observability;
    private readonly ILogger<CostAnalysisTimerFunction> _logger;

    public CostAnalysisTimerFunction(
        CostAnalysisOrchestrator orchestrator,
        SubscriptionDiscoveryService subscriptionDiscovery,
        QueueService queueService,
        ObservabilityService observability,
        ILogger<CostAnalysisTimerFunction> logger)
    {
        _orchestrator = orchestrator;
        _subscriptionDiscovery = subscriptionDiscovery;
        _queueService = queueService;
        _observability = observability;
        _logger = logger;
    }

    [Function("CostAnalysisTimer")]
    public async Task RunAsync(
        [TimerTrigger("%CostAnalysisSchedule%")] TimerInfo timer,
        FunctionContext context)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogInformation("CostAnalysisTimer started at {time}", startTime);

        try
        {
            var subscriptionIds = await _subscriptionDiscovery.DiscoverSubscriptionsAsync();
            _logger.LogInformation("Discovered {count} subscriptions", subscriptionIds.Count);

            foreach (var subscriptionId in subscriptionIds)
            {
                try
                {
                    if (_queueService.IsQueueProcessingEnabled)
                    {
                        var queueSent = await _queueService.SendSubscriptionAnalysisAsync(subscriptionId, "complete");
                        if (!queueSent)
                        {
                            await ExecuteCompleteAnalysisAsync(subscriptionId);
                        }
                    }
                    else
                    {
                        await ExecuteCompleteAnalysisAsync(subscriptionId);
                    }

                    _logger.LogInformation("Subscription {subscriptionId} processed successfully", subscriptionId);
                }
                catch (Exception subEx)
                {
                    _logger.LogError(subEx, "Error processing subscription {subscriptionId}", subscriptionId);
                }
            }

            var executionTime = DateTime.UtcNow - startTime;
            _observability.RecordAnalyzerExecutionTime("TimerOrchestrator", executionTime, true);
        }
        catch (Exception ex)
        {
            var executionTime = DateTime.UtcNow - startTime;
            _observability.RecordError("CostAnalysisTimer", ex);
            _observability.RecordAnalyzerExecutionTime("TimerOrchestrator", executionTime, false);
            _logger.LogError(ex, "Error executing CostAnalysisTimer");
            throw;
        }
    }

    private async Task ExecuteCompleteAnalysisAsync(string subscriptionId)
    {
        await _orchestrator.AnalyzeSubscriptionAsync(subscriptionId, "complete", false);
    }
}
