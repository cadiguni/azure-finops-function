namespace Personal.FinOpsApi.AzureFunctions.Models;

/// <summary>
/// Resultado completo da análise de anomalias de custo diário
/// </summary>
public class CostAnomalyReport
{
    // Legacy field kept for compatibility
    public string Date { get; set; } = string.Empty;
    public string RequestedDate { get; set; } = string.Empty;
    public string AnalysisDate { get; set; } = string.Empty;
    public string BaselineStartDate { get; set; } = string.Empty;
    public string BaselineEndDate { get; set; } = string.Empty;
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
    public string RequestedDate { get; set; } = string.Empty;
    public string AnalysisDate { get; set; } = string.Empty;
    public string BaselineStartDate { get; set; } = string.Empty;
    public string BaselineEndDate { get; set; } = string.Empty;
    public string CostSource { get; set; } = CostDataSources.CostManagementQueryApi;
    public string CostDataAsOf { get; set; } = string.Empty;
    public bool CostUsedFallback { get; set; }
    public string? CostFallbackReason { get; set; }

    // Custos
    public decimal LatestDailyCost { get; set; }
    public decimal BaselineAverage { get; set; }
    public string DataStatus { get; set; } = CostAnomalyDataStatus.NoCostData;
    // Legacy aliases kept for compatibility
    public decimal TodayCost
    {
        get => LatestDailyCost;
        set => LatestDailyCost = value;
    }
    public decimal AverageLast3Days
    {
        get => BaselineAverage;
        set => BaselineAverage = value;
    }

    // Variação contra histórico
    public decimal IncreaseAmount { get; set; }
    public decimal IncreasePercent { get; set; }

    // Projeção mensal
    public decimal MonthlyProjection { get; set; }
    public decimal ProjectedOverBudget { get; set; }

    // Visão do ciclo atual da assinatura (mês corrente)
    public decimal CurrentCycleCost { get; set; }
    public string CurrentCycleStartDate { get; set; } = string.Empty;
    public string CurrentCycleTurnDate { get; set; } = string.Empty;
    public int CurrentCycleElapsedDays { get; set; }
    public int CurrentCycleRemainingDays { get; set; }
    public decimal CurrentCycleDailyAverage { get; set; }
    public string CurrentCycleSource { get; set; } = BillingPeriodSources.DefaultCalendarMonth;
    public string CurrentCycleConfidence { get; set; } = BillingPeriodConfidence.Low;
    public string CurrentCycleNote { get; set; } = string.Empty;

    // Classificação
    public string Severity { get; set; } = "None";
    public bool HasAnomaly { get; set; }
    public List<string> Reasons { get; set; } = new();

    // Detalhes do período
    public List<DailyCostEntry> DailyCosts { get; set; } = new();
}

public sealed class SubscriptionBillingCycleWindow
{
    public DateTime CycleStartDate { get; init; }
    public DateTime CycleTurnDate { get; init; }
    public string Source { get; init; } = BillingPeriodSources.DefaultCalendarMonth;
    public string Confidence { get; init; } = BillingPeriodConfidence.Low;
    public string Note { get; init; } = string.Empty;

    public int CycleLengthDays => Math.Max(1, (CycleTurnDate.Date - CycleStartDate.Date).Days);
}

public sealed class BillingPeriodInfo
{
    public string SubscriptionId { get; init; } = string.Empty;
    public string CurrentPeriodFrom { get; init; } = string.Empty;
    public string CurrentPeriodTo { get; init; } = string.Empty;
    public int BillingCycleStartDay { get; init; }
    public string Source { get; init; } = BillingPeriodSources.DefaultCalendarMonth;
    public string Confidence { get; init; } = BillingPeriodConfidence.Low;
    public string Note { get; init; } = string.Empty;
}

public sealed class BillingPeriodOverrideInfo
{
    public string SubscriptionId { get; init; } = string.Empty;
    public int BillingCycleStartDay { get; init; }
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
    public string UpdatedBy { get; init; } = "unknown";
}

public sealed class BillingPeriodOverrideConfig
{
    public int Version { get; set; } = 1;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = "system";
    public List<BillingPeriodOverrideInfo> Overrides { get; set; } = new();
}

public sealed class BillingPeriodOfficialWindowInfo
{
    public string SubscriptionId { get; init; } = string.Empty;
    public string CurrentPeriodFrom { get; init; } = string.Empty;
    public string CurrentPeriodTo { get; init; } = string.Empty;
    public DateTime RetrievedAt { get; init; } = DateTime.UtcNow;
    public string RetrievedBySource { get; init; } = BillingPeriodSources.AzureBillingPeriodsApi;
}

public sealed class BillingPeriodOfficialCacheConfig
{
    public int Version { get; set; } = 1;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<BillingPeriodOfficialWindowInfo> Windows { get; set; } = new();
}

/// <summary>
/// Custo diário de uma subscription para um dia específico
/// </summary>
public class DailyCostEntry
{
    public string? Revision { get; set; }
    public string CostBasis { get; set; } = "ActualCost";
    public string Date { get; set; } = string.Empty;
    public decimal TotalCost { get; set; }
    public string Currency { get; set; } = "BRL";
    public string Source { get; set; } = CostDataSources.CostManagementQueryApi;
    public bool UsedFallback { get; set; }
    public string? FallbackReason { get; set; }
}

public sealed class CostAnomalyRunRequest
{
    public DateTime RequestedAt { get; set; }
    public string TriggeredBy { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
}

public sealed class CostAnomalyRunManifest
{
    public string RunId { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public string RequestedDate { get; set; } = string.Empty;
    public string TriggeredBy { get; set; } = string.Empty;
    public string Status { get; set; } = CostAnomalyRunStatuses.Running;
    public int CompletedSubscriptions { get; set; }
    public int FailedSubscriptions { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? SummaryGeneratedAt { get; set; }
    public CostAnomalyConfig Config { get; set; } = new();
    public List<CostAnomalyRunSubscription> Subscriptions { get; set; } = new();
}

public sealed class CostAnomalyRunSubscription
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;
}

public sealed class CostAnomalySubscriptionWorkItem
{
    public string RunId { get; set; } = string.Empty;
    public int SubscriptionIndex { get; set; }
}

public static class CostAnomalyRunStatuses
{
    public const string Running = "Running";
    public const string Completed = "Completed";
}

/// <summary>
/// Configurações da análise de anomalias (carregadas via App Settings)
/// </summary>
public class CostAnomalyConfig
{
    public decimal MonthlyBudget { get; set; } = 860m;
    public bool IgnoreCurrentDay { get; set; } = true;
    public int LookbackDays { get; set; } = 7;
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
            IgnoreCurrentDay = bool.TryParse(config["CostAnomalyIgnoreCurrentDay"], out var icd) ? icd : true,
            LookbackDays = int.TryParse(config["CostAnomalyLookbackDays"], out var lbd) ? Math.Clamp(lbd, 3, 30) : 7,
            BaselineDays = int.TryParse(config["CostAnomalyBaselineDays"], out var bd) ? Math.Clamp(bd, 1, 14) : 3,
            MinimumIncreaseAmount = decimal.TryParse(config["CostAnomalyMinimumIncreaseAmount"], out var mia) ? mia : 10m,
            MediumPercent = decimal.TryParse(config["CostAnomalyMediumPercent"], out var mp) ? mp : 30m,
            HighPercent = decimal.TryParse(config["CostAnomalyHighPercent"], out var hp) ? hp : 60m,
            CriticalPercent = decimal.TryParse(config["CostAnomalyCriticalPercent"], out var cp) ? cp : 100m,
        };
    }
}

public static class CostAnomalyDataStatus
{
    public const string Consolidated = "Consolidated";
    public const string InsufficientData = "InsufficientData";
    public const string NoCostData = "NoCostData";
    public const string QueryFailed = "QueryFailed";
}

public static class CurrentCycleSources
{
    public const string CalendarMonth = "CalendarMonth";
    public const string AzureBillingPeriod = "AzureBillingPeriod";
    public const string CostManagementBillingMonthToDate = "CostManagementBillingMonthToDate";
    public const string SubscriptionOverride = "SubscriptionOverride";
}

public static class BillingPeriodSources
{
    public const string AzureBillingPeriodsApi = "AzureBillingPeriodsApi";
    public const string ManualConfiguration = "ManualConfiguration";
    public const string DefaultCalendarMonth = "DefaultCalendarMonth";
}

public static class BillingPeriodConfidence
{
    public const string High = "High";
    public const string Medium = "Medium";
    public const string Low = "Low";
}
