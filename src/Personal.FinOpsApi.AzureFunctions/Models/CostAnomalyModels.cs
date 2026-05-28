namespace Personal.FinOpsApi.AzureFunctions.Models;

/// <summary>
/// Resultado completo da análise de anomalias de custo diário
/// </summary>
public class CostAnomalyReport
{
    public string Date { get; set; } = string.Empty;
    public string Currency { get; set; } = "BRL";
    public decimal MonthlyBudget { get; set; }
    public decimal DailyBudget { get; set; }
    public int BaselineDays { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public int TotalSubscriptionsAnalyzed { get; set; }
    public int TotalAnomaliesDetected { get; set; }
    public List<SubscriptionCostAnomaly> Subscriptions { get; set; } = new();
}

/// <summary>
/// Anomalia de custo detectada para uma subscription específica
/// </summary>
public class SubscriptionCostAnomaly
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;

    // Custos
    public decimal TodayCost { get; set; }
    public decimal AverageLast3Days { get; set; }

    // Variação contra histórico
    public decimal IncreaseAmount { get; set; }
    public decimal IncreasePercent { get; set; }

    // Projeção mensal
    public decimal MonthlyProjection { get; set; }
    public decimal ProjectedOverBudget { get; set; }

    // Classificação
    public string Severity { get; set; } = "None";
    public bool HasAnomaly { get; set; }
    public List<string> Reasons { get; set; } = new();

    // Detalhes do período
    public List<DailyCostEntry> DailyCosts { get; set; } = new();
}

/// <summary>
/// Custo diário de uma subscription para um dia específico
/// </summary>
public class DailyCostEntry
{
    public string Date { get; set; } = string.Empty;
    public decimal TotalCost { get; set; }
    public string Currency { get; set; } = "BRL";
}

/// <summary>
/// Configurações da análise de anomalias (carregadas via App Settings)
/// </summary>
public class CostAnomalyConfig
{
    public decimal MonthlyBudget { get; set; } = 860m;
    public int BaselineDays { get; set; } = 3;
    public decimal MinimumIncreaseAmount { get; set; } = 10m;
    public decimal MediumPercent { get; set; } = 30m;
    public decimal HighPercent { get; set; } = 60m;
    public decimal CriticalPercent { get; set; } = 100m;

    public decimal DailyBudget => MonthlyBudget / 30m;

    public static CostAnomalyConfig FromConfiguration(Microsoft.Extensions.Configuration.IConfiguration config)
    {
        return new CostAnomalyConfig
        {
            MonthlyBudget = decimal.TryParse(config["CostAnomalyMonthlyBudget"], out var mb) ? mb : 860m,
            BaselineDays = int.TryParse(config["CostAnomalyBaselineDays"], out var bd) ? bd : 3,
            MinimumIncreaseAmount = decimal.TryParse(config["CostAnomalyMinimumIncreaseAmount"], out var mia) ? mia : 10m,
            MediumPercent = decimal.TryParse(config["CostAnomalyMediumPercent"], out var mp) ? mp : 30m,
            HighPercent = decimal.TryParse(config["CostAnomalyHighPercent"], out var hp) ? hp : 60m,
            CriticalPercent = decimal.TryParse(config["CostAnomalyCriticalPercent"], out var cp) ? cp : 100m,
        };
    }
}
