using System.Text.Json.Serialization;

namespace Gvdasa.FinOpsApi.AzureFunctions.Models;

/// <summary>
/// Resultado completo de uma análise FinOps
/// </summary>
public class FinOpsAnalysisResult
{
    public Guid AnalysisId { get; set; }
    public DateTime ExecutedAt { get; set; }
    public string SubscriptionId { get; set; } = "";
    public string? ManagementGroupId { get; set; }
    public bool DryRun { get; set; }
    public int AnalysisPeriodDays { get; set; }

    public AnalysisSummary Summary { get; set; } = new();
    public List<CostRecommendation> Recommendations { get; set; } = new();
}

/// <summary>
/// Resumo agregado da análise FinOps
/// </summary>
public class AnalysisSummary
{
    public int TotalResourcesAnalyzed { get; set; }
    public int TotalRecommendations { get; set; }
    public decimal TotalEstimatedMonthlySavings { get; set; }
    public Dictionary<string, SummaryByType> ByType { get; set; } = new();
}

/// <summary>
/// Resumo por tipo de recomendação
/// </summary>
public class SummaryByType
{
    public int Count { get; set; }
    public decimal EstimatedMonthlySavings { get; set; }
}