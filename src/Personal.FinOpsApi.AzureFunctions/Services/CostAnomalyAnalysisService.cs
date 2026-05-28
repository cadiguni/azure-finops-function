using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Aplica regras de detecção de anomalias de custo.
/// 
/// Regra 1 — Custo acima da média recente (últimos N dias)
///   Medium: +30% | High: +60% | Critical: +100%
///   Filtro: diferença absoluta >= R$ 10 (evitar falso positivo)
/// 
/// Regra 2 — Custo acima da meta diária (budget mensal / 30)
///   Medium: acima da meta | High: +25% acima | Critical: +50% acima
/// 
/// Regra 3 — Projeção mensal acima do budget
///   Projeção = custo médio diário atual × 30
/// </summary>
public class CostAnomalyAnalysisService
{
    private readonly ILogger<CostAnomalyAnalysisService> _logger;

    public CostAnomalyAnalysisService(ILogger<CostAnomalyAnalysisService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analisa custos diários de uma subscription e detecta anomalias
    /// </summary>
    public SubscriptionCostAnomaly Analyze(
        string subscriptionId,
        string subscriptionName,
        List<DailyCostEntry> dailyCosts,
        CostAnomalyConfig config)
    {
        var today = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");

        var anomaly = new SubscriptionCostAnomaly
        {
            SubscriptionId = subscriptionId,
            SubscriptionName = subscriptionName,
            DailyCosts = dailyCosts
        };

        // Custo de hoje
        var todayEntry = dailyCosts.FirstOrDefault(d => d.Date == today);
        anomaly.TodayCost = todayEntry?.TotalCost ?? 0;

        // Média dos últimos N dias (excluindo hoje)
        var baselineCosts = dailyCosts
            .Where(d => d.Date != today)
            .OrderByDescending(d => d.Date)
            .Take(config.BaselineDays)
            .ToList();

        anomaly.AverageLast3Days = baselineCosts.Count > 0
            ? baselineCosts.Average(d => d.TotalCost)
            : 0;

        // Variação contra histórico
        anomaly.IncreaseAmount = anomaly.TodayCost - anomaly.AverageLast3Days;
        anomaly.IncreasePercent = anomaly.AverageLast3Days > 0
            ? (anomaly.IncreaseAmount / anomaly.AverageLast3Days) * 100
            : 0;

        // Projeção mensal (baseada na média incluindo hoje)
        var allCosts = dailyCosts.Where(d => d.TotalCost > 0).ToList();
        var avgDaily = allCosts.Count > 0 ? allCosts.Average(d => d.TotalCost) : 0;
        anomaly.MonthlyProjection = avgDaily * 30;
        anomaly.ProjectedOverBudget = anomaly.MonthlyProjection - config.MonthlyBudget;

        // --- Aplicar Regras ---
        var severity = "None";

        // Regra 1: Variação contra histórico recente
        if (anomaly.IncreasePercent >= config.MediumPercent && anomaly.IncreaseAmount >= config.MinimumIncreaseAmount)
        {
            if (anomaly.IncreasePercent >= config.CriticalPercent)
            {
                severity = "Critical";
                anomaly.Reasons.Add($"Custo diário {anomaly.IncreasePercent:F0}% acima da média dos últimos {config.BaselineDays} dias (R$ {anomaly.AverageLast3Days:F2} → R$ {anomaly.TodayCost:F2})");
            }
            else if (anomaly.IncreasePercent >= config.HighPercent)
            {
                severity = "High";
                anomaly.Reasons.Add($"Custo diário {anomaly.IncreasePercent:F0}% acima da média dos últimos {config.BaselineDays} dias (R$ {anomaly.AverageLast3Days:F2} → R$ {anomaly.TodayCost:F2})");
            }
            else
            {
                severity = MaxSeverity(severity, "Medium");
                anomaly.Reasons.Add($"Custo diário {anomaly.IncreasePercent:F0}% acima da média dos últimos {config.BaselineDays} dias (R$ {anomaly.AverageLast3Days:F2} → R$ {anomaly.TodayCost:F2})");
            }
        }

        // Regra 2: Custo acima da meta diária
        if (anomaly.TodayCost > config.DailyBudget)
        {
            var overBudgetPercent = ((anomaly.TodayCost - config.DailyBudget) / config.DailyBudget) * 100;

            if (overBudgetPercent >= 50)
            {
                severity = MaxSeverity(severity, "Critical");
                anomaly.Reasons.Add($"Custo diário R$ {anomaly.TodayCost:F2} está {overBudgetPercent:F0}% acima da meta diária de R$ {config.DailyBudget:F2}");
            }
            else if (overBudgetPercent >= 25)
            {
                severity = MaxSeverity(severity, "High");
                anomaly.Reasons.Add($"Custo diário R$ {anomaly.TodayCost:F2} está {overBudgetPercent:F0}% acima da meta diária de R$ {config.DailyBudget:F2}");
            }
            else
            {
                severity = MaxSeverity(severity, "Medium");
                anomaly.Reasons.Add($"Custo diário R$ {anomaly.TodayCost:F2} acima da meta diária de R$ {config.DailyBudget:F2}");
            }
        }

        // Regra 3: Projeção mensal acima do budget
        if (anomaly.ProjectedOverBudget > 0)
        {
            severity = MaxSeverity(severity, "Medium");
            anomaly.Reasons.Add($"Projeção mensal de R$ {anomaly.MonthlyProjection:F2} ultrapassa o budget de R$ {config.MonthlyBudget:F2} em R$ {anomaly.ProjectedOverBudget:F2}");
        }

        anomaly.Severity = severity;
        anomaly.HasAnomaly = severity != "None";

        if (anomaly.HasAnomaly)
        {
            _logger.LogWarning(
                "[COST-ANOMALY] {severity} para {subscriptionName} ({subscriptionId}): Hoje=R$ {todayCost:F2}, Média=R$ {avg:F2}, Projeção=R$ {projection:F2}",
                severity, subscriptionName, subscriptionId,
                anomaly.TodayCost, anomaly.AverageLast3Days, anomaly.MonthlyProjection);
        }
        else
        {
            _logger.LogInformation(
                "[COST-ANOMALY] OK para {subscriptionName}: Hoje=R$ {todayCost:F2}, Média=R$ {avg:F2}",
                subscriptionName, anomaly.TodayCost, anomaly.AverageLast3Days);
        }

        return anomaly;
    }

    private static string MaxSeverity(string current, string candidate)
    {
        var order = new Dictionary<string, int>
        {
            { "None", 0 },
            { "Medium", 1 },
            { "High", 2 },
            { "Critical", 3 }
        };

        var currentLevel = order.GetValueOrDefault(current, 0);
        var candidateLevel = order.GetValueOrDefault(candidate, 0);

        return candidateLevel > currentLevel ? candidate : current;
    }
}
