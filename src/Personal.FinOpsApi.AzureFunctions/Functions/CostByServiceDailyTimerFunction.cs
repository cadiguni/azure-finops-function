using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class CostByServiceDailyTimerFunction
{
    private readonly ICostManagementClient _costManagementClient;
    private readonly ICostStorageRepository _costStorageRepository;
    private readonly SubscriptionDiscoveryService _subscriptionDiscoveryService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostByServiceDailyTimerFunction> _logger;

    public CostByServiceDailyTimerFunction(
        ICostManagementClient costManagementClient,
        ICostStorageRepository costStorageRepository,
        SubscriptionDiscoveryService subscriptionDiscoveryService,
        IConfiguration configuration,
        ILogger<CostByServiceDailyTimerFunction> logger)
    {
        _costManagementClient = costManagementClient;
        _costStorageRepository = costStorageRepository;
        _subscriptionDiscoveryService = subscriptionDiscoveryService;
        _configuration = configuration;
        _logger = logger;
    }

    [Function("CostByServiceDailyTimer")]
    public async Task RunAsync([TimerTrigger("%CostByServiceDailySchedule%")] TimerInfo timer)
    {
        if (!CostCollectionRouting.IsLegacyByServiceEnabled(_configuration))
        {
            _logger.LogInformation(
                "Coleta legada por serviço desabilitada: source={source}",
                CostDataSources.CostManagementQueryApi);
            return;
        }

        var targetDate = ResolveTargetDate();
        var resolvedSubscriptions = await ResolveSubscriptionsAsync();
        var subscriptions = CostCollectionRouting.FilterLegacySubscriptions(
            _configuration,
            resolvedSubscriptions);
        var excludedCount = resolvedSubscriptions.Count - subscriptions.Count;
        var successCount = 0;
        var failureCount = 0;

        _logger.LogInformation(
            "Iniciando coleta de custo por serviço: source={source} date={date} subscriptions={count} excludedExportSubscriptions={excludedCount}",
            CostDataSources.CostManagementQueryApi,
            targetDate.ToString("yyyy-MM-dd"),
            subscriptions.Count,
            excludedCount);

        foreach (var subscriptionId in subscriptions)
        {
            try
            {
                var queryResult = await _costManagementClient.QueryCostByServiceAsync(
                    subscriptionId,
                    targetDate,
                    targetDate,
                    granularity: "None");

                if (queryResult.Rows.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Cost Management não retornou dados consolidados para {subscriptionId} em {targetDate:yyyy-MM-dd}.");
                }

                var byServiceRows = queryResult.Rows
                    .GroupBy(r => new { r.Label, r.Currency })
                    .Select(g => new CostByServiceRow
                    {
                        Label = g.Key.Label,
                        Currency = g.Key.Currency,
                        TotalCost = g.Sum(x => x.TotalCost),
                        Count = g.Sum(x => x.Count),
                        SubscriptionId = subscriptionId
                    })
                    .OrderByDescending(r => r.TotalCost)
                    .ToList();

                await _costStorageRepository.SaveByServiceAsync(
                    targetDate,
                    subscriptionId,
                    byServiceRows,
                    queryResult.RawJson);

                successCount++;
                _logger.LogInformation(
                    "Coleta concluída para {subscriptionId}: {rows} linhas",
                    subscriptionId,
                    byServiceRows.Count);
            }
            catch (Exception ex)
            {
                failureCount++;
                _logger.LogError(
                    ex,
                    "Falha na coleta de custo por serviço para subscription {subscriptionId}. Continuando com as demais.",
                    subscriptionId);
            }
        }

        _logger.LogInformation(
            "Fim da coleta de custo por serviço para {date}. Sucesso: {successCount}, Falhas: {failureCount}",
            targetDate.ToString("yyyy-MM-dd"),
            successCount,
            failureCount);
    }

    private DateTime ResolveTargetDate()
    {
        var delayDays = Math.Clamp(
            _configuration.GetValue<int?>("COST_DATA_DELAY_DAYS") ?? 2,
            1,
            7);

        return DateTime.UtcNow.Date.AddDays(-delayDays);
    }

    private async Task<List<string>> ResolveSubscriptionsAsync()
    {
        var raw = _configuration["COST_SUBSCRIPTIONS"];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        try
        {
            var discovered = await _subscriptionDiscoveryService.DiscoverSubscriptionsAsync();
            if (discovered.Count > 0)
            {
                _logger.LogInformation(
                    "COST_SUBSCRIPTIONS não definido. Usando discovery automático: {count} subscriptions.",
                    discovered.Count);
                return discovered
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha no discovery automático de subscriptions para custo por serviço.");
        }

        var single = _configuration["AZURE_SUBSCRIPTION_ID"];
        if (!string.IsNullOrWhiteSpace(single))
        {
            return new List<string> { single.Trim() };
        }

        _logger.LogWarning("Nenhuma subscription configurada para COST_SUBSCRIPTIONS/AZURE_SUBSCRIPTION_ID.");
        return new List<string>();
    }
}
