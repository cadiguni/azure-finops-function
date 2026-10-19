using Personal.FinOpsApi.AzureFunctions.Models;
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
        if (!CostCollectionRouting.IsLegacyByResourceEnabled(_configuration))
        {
            _logger.LogInformation(
                "Coleta legada por recurso desabilitada: source={source}",
                CostDataSources.CostManagementQueryApi);
            return;
        }

        var delayDays = Math.Clamp(
            _configuration.GetValue<int?>("COST_DATA_DELAY_DAYS") ?? 2,
            1,
            7);
        var targetDate = DateTime.UtcNow.Date.AddDays(-delayDays);
        // A grade de recursos representa toda a assinatura. Filtrar App Service aqui fazia
        // com que os snapshots novos parecessem incompletos, mesmo quando a assinatura tinha
        // custo em outros serviços. Um filtro continua possível como configuração explícita.
        var serviceName = NormalizeServiceFilter(_configuration["COST_RESOURCE_SERVICE"] ?? "all");

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
