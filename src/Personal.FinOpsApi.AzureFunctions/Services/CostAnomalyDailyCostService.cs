using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Consulta custos diários das subscriptions via Cost Management API.
/// Retorna custo total por dia para cada subscription no período solicitado.
/// </summary>
public class CostAnomalyDailyCostService
{
    private readonly ICostManagementClient _costManagementClient;
    private readonly ILogger<CostAnomalyDailyCostService> _logger;

    public CostAnomalyDailyCostService(
        ICostManagementClient costManagementClient,
        ILogger<CostAnomalyDailyCostService> logger)
    {
        _costManagementClient = costManagementClient;
        _logger = logger;
    }

    /// <summary>
    /// Busca custo diário total de uma subscription nos últimos N dias + hoje
    /// </summary>
    public async Task<List<DailyCostEntry>> GetDailyCostsAsync(
        string subscriptionId, 
        int baselineDays, 
        CancellationToken cancellationToken = default)
    {
        // Período: de (hoje - baselineDays) até hoje
        var today = DateTime.UtcNow.Date;
        var startDate = today.AddDays(-baselineDays);

        _logger.LogInformation(
            "[COST-ANOMALY] Consultando custos diários para {subscriptionId}: {start} a {end}",
            subscriptionId, startDate.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"));

        try
        {
            // Usa granularidade Daily para ter custo por dia (sem agrupamento por serviço)
            var response = await _costManagementClient.QueryCostByServiceAsync(
                subscriptionId,
                startDate,
                today,
                granularity: "Daily",
                cancellationToken: cancellationToken);

            // Agrupa por data (soma de todos os serviços no dia)
            var dailyCosts = response.Rows
                .Where(r => r.UsageDate.HasValue)
                .GroupBy(r => r.UsageDate!.Value.Date)
                .Select(g => new DailyCostEntry
                {
                    Date = g.Key.ToString("yyyy-MM-dd"),
                    TotalCost = g.Sum(r => r.TotalCost),
                    Currency = g.FirstOrDefault()?.Currency ?? "BRL"
                })
                .OrderBy(d => d.Date)
                .ToList();

            _logger.LogInformation(
                "[COST-ANOMALY] {count} dias de custo obtidos para {subscriptionId}. Total hoje: {todayCost:F2}",
                dailyCosts.Count, subscriptionId,
                dailyCosts.LastOrDefault()?.TotalCost ?? 0);

            return dailyCosts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[COST-ANOMALY] Erro ao consultar custos diários para {subscriptionId}", subscriptionId);
            return new List<DailyCostEntry>();
        }
    }
}
