using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Aplica regras de detecção de anomalias de custo.
/// 
/// Regra 1 — Custo acima da média recente (últimos N dias)
///   Medium: +30% | High: +60% | Critical: +100%
///   Filtro: diferença absoluta >= R$ 10 (evitar falso positivo)
/// 
/// Regra 2 — Custo acima da meta diária (budget do ciclo / dias do ciclo)
///   Medium: acima da meta | High: +25% acima | Critical: +50% acima
/// 
/// Regra 3 — Projeção até a virada do ciclo acima do budget
///   Projeção = custo acumulado no ciclo + média diária do ciclo × dias restantes
/// </summary>
public class CostAnomalyAnalysisService
{
    private const decimal MinimumCostForMeaningfulPercent = 1.0m;

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
        DateTime requestedDate,
        CostAnomalyConfig config,
        SubscriptionBillingCycleWindow? billingCycleWindow = null)
    {
        var requestedDateText = requestedDate.ToString("yyyy-MM-dd");

        var anomaly = new SubscriptionCostAnomaly
        {
            SubscriptionId = subscriptionId,
            SubscriptionName = subscriptionName,
            RequestedDate = requestedDateText,
            DailyCosts = dailyCosts,
            CostSource = ResolveCostSource(dailyCosts),
            CostUsedFallback = dailyCosts.Any(entry => entry.UsedFallback),
            CostFallbackReason = dailyCosts
                .Select(entry => entry.FallbackReason)
                .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason)),
            CostDataAsOf = dailyCosts
                .Select(entry => entry.Date)
                .OrderByDescending(date => date, StringComparer.Ordinal)
                .FirstOrDefault() ?? string.Empty
        };

        if (dailyCosts.Count == 0)
        {
            anomaly.DataStatus = CostAnomalyDataStatus.NoCostData;
            anomaly.Reasons.Add("Nenhum custo diário disponível na janela consultada.");
            _logger.LogInformation("[COST-ANOMALY] Sem dados de custo para {subscriptionName} ({subscriptionId})", subscriptionName, subscriptionId);
            return anomaly;
        }

        var costsByDate = dailyCosts
            .Select(d => new
            {
                Entry = d,
                ParsedDate = DateTime.TryParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed.Date
                    : DateTime.MinValue
            })
            .Where(x => x.ParsedDate != DateTime.MinValue)
            .GroupBy(x => x.ParsedDate)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Entry.TotalCost).First().Entry);

        var candidateDates = costsByDate.Keys
            .Where(d => d <= requestedDate.Date)
            .ToList();

        if (config.IgnoreCurrentDay)
        {
            var currentUtcDate = DateTime.UtcNow.Date;
            candidateDates = candidateDates.Where(d => d != currentUtcDate).ToList();
        }

        if (candidateDates.Count == 0)
        {
            anomaly.DataStatus = CostAnomalyDataStatus.NoCostData;
            anomaly.Reasons.Add("Nenhum custo diário consolidado disponível para análise.");
            _logger.LogInformation(
                "[COST-ANOMALY] Sem data consolidada para {subscriptionName} ({subscriptionId})",
                subscriptionName, subscriptionId);
            return anomaly;
        }

        var analysisDate = candidateDates.Max();
        var analysisDateText = analysisDate.ToString("yyyy-MM-dd");

        anomaly.DataStatus = CostAnomalyDataStatus.Consolidated;
        anomaly.AnalysisDate = analysisDateText;
        anomaly.LatestDailyCost = costsByDate[analysisDate].TotalCost;

        var currentCycleSource = BillingPeriodSources.DefaultCalendarMonth;
        var currentCycleConfidence = BillingPeriodConfidence.Low;
        var currentCycleNote = "Billing period oficial não encontrado; usando mês calendário.";
        var currentCycleStart = new DateTime(analysisDate.Year, analysisDate.Month, 1);
        var currentCycleTurn = currentCycleStart.AddMonths(1);
        if (billingCycleWindow is not null &&
            billingCycleWindow.CycleStartDate.Date <= analysisDate &&
            analysisDate < billingCycleWindow.CycleTurnDate.Date)
        {
            currentCycleStart = billingCycleWindow.CycleStartDate.Date;
            currentCycleTurn = billingCycleWindow.CycleTurnDate.Date;
            currentCycleSource = billingCycleWindow.Source;
            currentCycleConfidence = billingCycleWindow.Confidence;
            currentCycleNote = billingCycleWindow.Note;
        }

        var currentCycleElapsedDays = Math.Max(1, (analysisDate - currentCycleStart).Days + 1);
        var currentCycleRemainingDays = Math.Max(0, (currentCycleTurn - analysisDate).Days);
        var currentCycleLengthDays = Math.Max(1, (currentCycleTurn - currentCycleStart).Days);
        var currentCycleCost = costsByDate
            .Where(kvp => kvp.Key >= currentCycleStart && kvp.Key <= analysisDate)
            .Sum(kvp => kvp.Value.TotalCost);
        var currentCycleDailyAverage = currentCycleCost / currentCycleElapsedDays;
        var currentCycleDailyBudget = config.MonthlyBudget / currentCycleLengthDays;

        anomaly.CurrentCycleCost = currentCycleCost;
        anomaly.CurrentCycleStartDate = currentCycleStart.ToString("yyyy-MM-dd");
        anomaly.CurrentCycleTurnDate = currentCycleTurn.ToString("yyyy-MM-dd");
        anomaly.CurrentCycleElapsedDays = currentCycleElapsedDays;
        anomaly.CurrentCycleRemainingDays = currentCycleRemainingDays;
        anomaly.CurrentCycleDailyAverage = currentCycleDailyAverage;
        anomaly.CurrentCycleSource = currentCycleSource;
        anomaly.CurrentCycleConfidence = currentCycleConfidence;
        anomaly.CurrentCycleNote = currentCycleNote;
        anomaly.MonthlyProjection = currentCycleCost + (currentCycleDailyAverage * currentCycleRemainingDays);
        anomaly.ProjectedOverBudget = anomaly.MonthlyProjection - config.MonthlyBudget;

        var baselineStart = analysisDate.AddDays(-config.BaselineDays);
        var baselineEnd = analysisDate.AddDays(-1);
        anomaly.BaselineStartDate = baselineStart.ToString("yyyy-MM-dd");
        anomaly.BaselineEndDate = baselineEnd.ToString("yyyy-MM-dd");

        var baselineEntries = new List<DailyCostEntry>();
        for (var day = baselineStart; day <= baselineEnd; day = day.AddDays(1))
        {
            if (costsByDate.TryGetValue(day, out var entry))
            {
                baselineEntries.Add(entry);
            }
        }

        if (baselineEntries.Count < config.BaselineDays)
        {
            anomaly.DataStatus = CostAnomalyDataStatus.InsufficientData;
            anomaly.Reasons.Add(
                $"Histórico insuficiente: encontrados {baselineEntries.Count} de {config.BaselineDays} dia(s) no baseline ({anomaly.BaselineStartDate} a {anomaly.BaselineEndDate}).");
            _logger.LogInformation(
                "[COST-ANOMALY] Dados insuficientes para {subscriptionName} ({subscriptionId}): baseline {found}/{required}",
                subscriptionName, subscriptionId, baselineEntries.Count, config.BaselineDays);
            return anomaly;
        }

        anomaly.BaselineAverage = baselineEntries.Average(d => d.TotalCost);
        anomaly.IncreaseAmount = anomaly.LatestDailyCost - anomaly.BaselineAverage;
        anomaly.IncreasePercent = CalculateIncreasePercent(anomaly.LatestDailyCost, anomaly.BaselineAverage, anomaly.IncreaseAmount);

        if (anomaly.IncreaseAmount < config.MinimumIncreaseAmount)
        {
            anomaly.Severity = "None";
            anomaly.HasAnomaly = false;
            _logger.LogInformation(
                "[COST-ANOMALY] Sem alerta para {subscriptionName}: aumento absoluto R$ {increase:F2} abaixo do mínimo R$ {min:F2}",
                subscriptionName, anomaly.IncreaseAmount, config.MinimumIncreaseAmount);
            return anomaly;
        }

        // --- Aplicar Regras ---
        var severity = "None";

        // Regra 1: Variação contra histórico recente
        if (anomaly.BaselineAverage > 0 && anomaly.IncreasePercent >= config.MediumPercent)
        {
            if (anomaly.IncreasePercent >= config.CriticalPercent)
            {
                severity = "Critical";
                anomaly.Reasons.Add($"Último custo diário disponível está {anomaly.IncreasePercent:F0}% acima da média dos {config.BaselineDays} dias anteriores.");
            }
            else if (anomaly.IncreasePercent >= config.HighPercent)
            {
                severity = "High";
                anomaly.Reasons.Add($"Último custo diário disponível está {anomaly.IncreasePercent:F0}% acima da média dos {config.BaselineDays} dias anteriores.");
            }
            else
            {
                severity = MaxSeverity(severity, "Medium");
                anomaly.Reasons.Add($"Último custo diário disponível está {anomaly.IncreasePercent:F0}% acima da média dos {config.BaselineDays} dias anteriores.");
            }
        }

        // Regra 2: Custo acima da meta diária
        if (anomaly.LatestDailyCost > currentCycleDailyBudget)
        {
            var overBudgetPercent = ((anomaly.LatestDailyCost - currentCycleDailyBudget) / currentCycleDailyBudget) * 100;

            if (overBudgetPercent >= 50)
            {
                severity = MaxSeverity(severity, "Critical");
                anomaly.Reasons.Add($"Último custo diário disponível está {overBudgetPercent:F0}% acima da meta diária do ciclo de R$ {currentCycleDailyBudget:F2}.");
            }
            else if (overBudgetPercent >= 25)
            {
                severity = MaxSeverity(severity, "High");
                anomaly.Reasons.Add($"Último custo diário disponível está {overBudgetPercent:F0}% acima da meta diária do ciclo de R$ {currentCycleDailyBudget:F2}.");
            }
            else
            {
                severity = MaxSeverity(severity, "Medium");
                anomaly.Reasons.Add($"Último custo diário disponível está acima da meta diária do ciclo de R$ {currentCycleDailyBudget:F2}.");
            }
        }

        // Regra 3: Projeção até a virada do ciclo acima do budget
        if (anomaly.ProjectedOverBudget > 0)
        {
            severity = MaxSeverity(severity, "Medium");
            anomaly.Reasons.Add($"Projeção até {anomaly.CurrentCycleTurnDate} de R$ {anomaly.MonthlyProjection:F2} ultrapassa o budget de R$ {config.MonthlyBudget:F2} em R$ {anomaly.ProjectedOverBudget:F2}");
        }

        anomaly.Severity = severity;
        anomaly.HasAnomaly = severity != "None";

        if (anomaly.HasAnomaly)
        {
            _logger.LogWarning(
                "[COST-ANOMALY] {severity} para {subscriptionName} ({subscriptionId}): analysisDate={analysisDate}, latestDailyCost=R$ {latestDailyCost:F2}, baselineAverage=R$ {baselineAverage:F2}",
                severity, subscriptionName, subscriptionId,
                anomaly.AnalysisDate, anomaly.LatestDailyCost, anomaly.BaselineAverage);
        }
        else
        {
            _logger.LogInformation(
                "[COST-ANOMALY] OK para {subscriptionName}: analysisDate={analysisDate}, latestDailyCost=R$ {latestDailyCost:F2}, baselineAverage=R$ {baselineAverage:F2}, status={status}",
                subscriptionName, anomaly.AnalysisDate, anomaly.LatestDailyCost, anomaly.BaselineAverage, anomaly.DataStatus);
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

    private static decimal CalculateIncreasePercent(decimal latestDailyCost, decimal baselineAverage, decimal increaseAmount)
    {
        // Evita percentuais explosivos em subscriptions com custo em centavos.
        if (latestDailyCost < MinimumCostForMeaningfulPercent && baselineAverage < MinimumCostForMeaningfulPercent)
        {
            return 0m;
        }

        return baselineAverage > 0
            ? (increaseAmount / baselineAverage) * 100
            : 0m;
    }

    private static string ResolveCostSource(IEnumerable<DailyCostEntry> dailyCosts)
    {
        var sources = dailyCosts
            .Select(entry => entry.Source)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return sources.Count switch
        {
            0 => CostDataSources.CostManagementQueryApi,
            1 => sources[0],
            _ => "Mixed"
        };
    }
}
