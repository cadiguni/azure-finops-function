using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class CostByResourceDailyTimerFunction
{
    private readonly QueueService _queueService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostByResourceDailyTimerFunction> _logger;

    public CostByResourceDailyTimerFunction(
        QueueService queueService,
        IConfiguration configuration,
        ILogger<CostByResourceDailyTimerFunction> logger)
    {
        _queueService = queueService;
        _configuration = configuration;
        _logger = logger;
    }

    [Function("CostByResourceDailyTimer")]
    public async Task RunAsync([TimerTrigger("%CostByResourceDailySchedule%")]
        TimerInfo timer)
    {
        var targetDate = DateTime.UtcNow.Date.AddDays(-1);
        var serviceName = NormalizeServiceFilter(_configuration["COST_RESOURCE_SERVICE"] ?? "Azure App Service");

        if (!_queueService.IsQueueProcessingEnabled)
        {
            _logger.LogWarning("Queue processing está desabilitado. CostByResourceDailyTimer não irá enfileirar mensagens.");
            return;
        }

        _logger.LogInformation("Iniciando starter by-resource para {date}. Service={service}",
            targetDate.ToString("yyyy-MM-dd"),
            serviceName ?? "all");

        var queued = await _queueService.SendCostByResourceStarterAsync(targetDate, serviceName, "all");

        _logger.LogInformation(
            "Fim do starter by-resource para {date}. queued={queued}",
            targetDate.ToString("yyyy-MM-dd"),
            queued);
    }

    private static string? NormalizeServiceFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();
    }
}
