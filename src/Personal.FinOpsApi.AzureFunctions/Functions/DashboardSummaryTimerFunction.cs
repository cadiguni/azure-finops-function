using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class DashboardSummaryTimerFunction
{
    private readonly SummaryGenerationService _summaryGenerationService;
    private readonly ILogger<DashboardSummaryTimerFunction> _logger;

    public DashboardSummaryTimerFunction(
        SummaryGenerationService summaryGenerationService,
        ILogger<DashboardSummaryTimerFunction> logger)
    {
        _summaryGenerationService = summaryGenerationService;
        _logger = logger;
    }

    [Function("DashboardSummaryTimer")]
    public async Task RunAsync([TimerTrigger("%DashboardSummarySchedule%")] TimerInfo timer)
    {
        var targetDate = DateTime.UtcNow.Date;
        _logger.LogInformation("[DASHBOARD-SUMMARY-TIMER] Iniciando geração de summaries para {date}", targetDate.ToString("yyyy-MM-dd"));
        await _summaryGenerationService.GenerateDailySummariesAsync(targetDate);
        _logger.LogInformation("[DASHBOARD-SUMMARY-TIMER] Geração concluída para {date}", targetDate.ToString("yyyy-MM-dd"));
    }
}
