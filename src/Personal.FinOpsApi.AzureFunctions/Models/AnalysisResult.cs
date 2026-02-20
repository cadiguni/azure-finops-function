using System.Text.Json.Serialization;

namespace Personal.FinOpsApi.AzureFunctions.Models;

/// <summary>
/// 📊 Modelo que representa o resultado completo de uma análise de custos
/// </summary>
public class FullAnalysisResult
{
    [JsonPropertyName("analysisId")]
    public string AnalysisId { get; set; } = string.Empty;
    
    [JsonPropertyName("executedAt")]
    public DateTime ExecutedAt { get; set; }
    
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;
    
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;
    
    [JsonPropertyName("managementGroupId")]
    public string? ManagementGroupId { get; set; }
    
    [JsonPropertyName("analysisPeriodDays")]
    public int AnalysisPeriodDays { get; set; }
    
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }
    
    [JsonPropertyName("recommendations")]
    public List<CostRecommendation> Recommendations { get; set; } = new();
    
    [JsonPropertyName("summary")]
    public FullAnalysisSummary Summary { get; set; } = new();
}

/// <summary>
/// 📈 Resumo da análise completa
/// </summary>
public class FullAnalysisSummary
{
    [JsonPropertyName("totalResourcesAnalyzed")]
    public int TotalResourcesAnalyzed { get; set; }
    
    [JsonPropertyName("totalRecommendations")]
    public int TotalRecommendations { get; set; }
    
    [JsonPropertyName("totalEstimatedMonthlySavings")]
    public decimal TotalEstimatedMonthlySavings { get; set; }
}