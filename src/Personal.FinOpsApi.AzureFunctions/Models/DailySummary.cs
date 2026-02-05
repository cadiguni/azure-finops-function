using System.Text.Json.Serialization;

namespace Personal.FinOpsApi.AzureFunctions.Models;

/// <summary>
/// Summary consolidado de análises de custo de um dia
/// </summary>
public class DailySummary
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "BRL";

    [JsonPropertyName("totalPotentialSavings")]
    public decimal TotalPotentialSavings { get; set; }

    [JsonPropertyName("totalResourcesAnalyzed")]
    public int TotalResourcesAnalyzed { get; set; }

    [JsonPropertyName("summaryByType")]
    public Dictionary<string, DailySummaryByType> SummaryByType { get; set; } = new();

    [JsonPropertyName("summaryBySubscription")]
    public Dictionary<string, DailySummaryBySubscription> SummaryBySubscription { get; set; } = new();

    [JsonPropertyName("top10")]
    public List<CostFinding> Top10 { get; set; } = new();

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; }

    [JsonPropertyName("dataSource")]
    public string DataSource { get; set; } = "FinOps-Analyzer";
}

/// <summary>
/// Agregação por tipo de recurso
/// </summary>
public class DailySummaryByType
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("potentialSavings")]
    public decimal PotentialSavings { get; set; }

    [JsonPropertyName("averageSavings")]
    public decimal AverageSavings => Count > 0 ? PotentialSavings / Count : 0;
}

/// <summary>
/// Agregação por subscription
/// </summary>
public class DailySummaryBySubscription
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("potentialSavings")]
    public decimal PotentialSavings { get; set; }

    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;
}
