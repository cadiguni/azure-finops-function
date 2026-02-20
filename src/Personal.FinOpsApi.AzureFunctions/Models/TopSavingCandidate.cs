using System.Text.Json.Serialization;

namespace Personal.FinOpsApi.AzureFunctions.Models;

/// <summary>
/// Candidato para Top 10 de economias - formato normalizado
/// </summary>
public class TopSavingCandidate
{
    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    [JsonPropertyName("resourceType")]
    public string ResourceType { get; set; } = string.Empty;

    [JsonPropertyName("resourceName")]
    public string ResourceName { get; set; } = string.Empty;

    [JsonPropertyName("resourceId")]
    public string ResourceId { get; set; } = string.Empty;

    [JsonPropertyName("estimatedMonthlySavings")]
    public decimal EstimatedMonthlySavings { get; set; }

    [JsonPropertyName("analyzerType")]
    public string AnalyzerType { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Wrapper para o Top 10 com metadados
/// </summary>
public class DailyTop10Result
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("totalSubscriptions")]
    public int TotalSubscriptions { get; set; }

    [JsonPropertyName("totalSavings")]
    public decimal TotalSavings { get; set; }

    [JsonPropertyName("top10")]
    public List<TopSavingCandidate> Top10 { get; set; } = new();
}