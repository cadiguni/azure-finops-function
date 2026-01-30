namespace Gvdasa.FinOpsApi.AzureFunctions.Models;

public class CostAnalysisResult
{
    public string AnalysisId { get; set; } = Guid.NewGuid().ToString();
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
    public string Scope { get; set; } = string.Empty;
    public string? SubscriptionId { get; set; }
    public string? ManagementGroupId { get; set; }
    public int AnalysisPeriodDays { get; set; }
    public bool DryRun { get; set; }
    
    /// <summary>
    /// Lista de recomendações de economia encontradas
    /// </summary>
    public List<CostRecommendation> Recommendations { get; set; } = new();
    
    /// <summary>
    /// Resumo consolidado
    /// </summary>
    public CostAnalysisSummary Summary { get; set; } = new();
}

public class CostRecommendation
{
    /// <summary>
    /// Tipo da recomendação
    /// </summary>
    public string Type { get; set; } = string.Empty;
    
    /// <summary>
    /// ID do recurso do Azure
    /// </summary>
    public string ResourceId { get; set; } = string.Empty;
    
    /// <summary>
    /// Nome do recurso
    /// </summary>
    public string ResourceName { get; set; } = string.Empty;
    
    /// <summary>
    /// Tipo do recurso (VM, Disk, etc.)
    /// </summary>
    public string ResourceType { get; set; } = string.Empty;
    
    /// <summary>
    /// Resource Group
    /// </summary>
    public string ResourceGroup { get; set; } = string.Empty;
    
    /// <summary>
    /// Subscription ID
    /// </summary>
    public string SubscriptionId { get; set; } = string.Empty;
    
    /// <summary>
    /// Economia estimada por mês (USD)
    /// </summary>
    public decimal EstimatedMonthlySavings { get; set; }
    
    /// <summary>
    /// Descrição da recomendação
    /// </summary>
    public string Description { get; set; } = string.Empty;
    
    /// <summary>
    /// Prioridade: High, Medium, Low
    /// </summary>
    public string Priority { get; set; } = "Medium";
    
    /// <summary>
    /// Tags do recurso
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();
}

public class CostAnalysisSummary
{
    /// <summary>
    /// Total de recursos analisados
    /// </summary>
    public int TotalResourcesAnalyzed { get; set; }
    
    /// <summary>
    /// Total de recomendações encontradas
    /// </summary>
    public int TotalRecommendations { get; set; }
    
    /// <summary>
    /// Economia total estimada por mês (USD)
    /// </summary>
    public decimal TotalEstimatedMonthlySavings { get; set; }
    
    /// <summary>
    /// Breakdown por tipo de recomendação
    /// </summary>
    public Dictionary<string, RecommendationTypeSummary> ByType { get; set; } = new();
}

public class RecommendationTypeSummary
{
    public int Count { get; set; }
    public decimal EstimatedMonthlySavings { get; set; }
}