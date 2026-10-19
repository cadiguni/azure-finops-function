using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Consulta custos diários das subscriptions pela fonte de custo configurada.
/// Retorna custo total por dia para cada subscription no período solicitado.
/// </summary>
public class CostAnomalyDailyCostService
{
    private readonly ICostDataReader _costDataReader;
    private readonly ILogger<CostAnomalyDailyCostService> _logger;

    public CostAnomalyDailyCostService(
        ICostDataReader costDataReader,
        ILogger<CostAnomalyDailyCostService> logger)
    {
        _costDataReader = costDataReader;
        _logger = logger;
    }

    /// <summary>
    /// Busca custo diário total de uma subscription em uma janela de dias.
    /// </summary>
    public async Task<List<DailyCostEntry>> GetDailyCostsAsync(
        string subscriptionId,
        DateTime requestedDate,
        int lookbackDays,
        CancellationToken cancellationToken = default)
    {
        var endDate = requestedDate.Date;
        var startDate = endDate.AddDays(-(Math.Max(lookbackDays, 1) - 1));

        _logger.LogInformation(
            "[COST-ANOMALY] Consultando custos diários para {subscriptionId}: {start} a {end}",
            subscriptionId, startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"));

        try
        {
            var result = await _costDataReader.ReadDailyCostsAsync(
                subscriptionId,
                startDate,
                endDate,
                cancellationToken);
            var dailyCosts = result.Entries.ToList();

            _logger.LogInformation(
                "[COST-ANOMALY] {count} dias de custo obtidos para {subscriptionId}. source={source} dataAsOf={dataAsOf} fallback={fallback} Último custo coletado: {latestCost:F2}",
                dailyCosts.Count,
                subscriptionId,
                result.Source,
                result.DataAsOf?.ToString("yyyy-MM-dd"),
                result.UsedFallback,
                dailyCosts.LastOrDefault()?.TotalCost ?? 0);

            return dailyCosts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[COST-ANOMALY] Erro ao consultar custos diários para {subscriptionId}", subscriptionId);
            throw;
        }
    }
}
