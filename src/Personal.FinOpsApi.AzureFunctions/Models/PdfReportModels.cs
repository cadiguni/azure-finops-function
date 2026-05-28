using System.ComponentModel.DataAnnotations;

namespace Personal.FinOpsApi.AzureFunctions.Models;

/// <summary>
/// Parâmetros para geração de relatório PDF
/// </summary>
public class PdfReportRequest
{
    /// <summary>
    /// Data do relatório (formato: yyyy-MM-dd)
    /// </summary>
    [Required]
    public string Date { get; set; } = string.Empty;

    /// <summary>
    /// Management Group ID (opcional - se não informado, pega todos)
    /// </summary>
    public string? ManagementGroupId { get; set; }

    /// <summary>
    /// Subscription ID (optional - se não informado, pega todas do Management Group)
    /// </summary>
    public string? SubscriptionId { get; set; }

    /// <summary>
    /// Período em meses para análise (default: 1)
    /// </summary>
    public int? AnalysisMonths { get; set; } = 1;

    /// <summary>
    /// Tipos de recomendações para incluir (opcional - se vazio, inclui todas)
    /// </summary>
    public List<string> RecommendationTypes { get; set; } = new();

    /// <summary>
    /// Incluir detalhes de governança no relatório
    /// </summary>
    public bool IncludeGovernanceDetails { get; set; } = true;

    /// <summary>
    /// Incluir gráficos e visualizações no PDF
    /// </summary>
    public bool IncludeCharts { get; set; } = true;

    /// <summary>
    /// Idioma do relatório (pt-BR, en-US)
    /// </summary>
    public string Language { get; set; } = "pt-BR";
}

/// <summary>
/// Estrutura do relatório para geração de PDF
/// </summary>
public class PdfReportData
{
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public DateTime AnalysisPeriodStart { get; set; }
    public DateTime AnalysisPeriodEnd { get; set; }
    public string ReportScope { get; set; } = string.Empty; // "ManagementGroup", "Subscription"
    public string ScopeName { get; set; } = string.Empty;

    // Página 1 - Resumo Executivo
    public ExecutiveSummary ExecutiveSummary { get; set; } = new();

    // Página 2 - Visão por Assinatura
    public List<SubscriptionInfo> SubscriptionBreakdown { get; set; } = new();

    // Página 3 - Principais Desperdícios
    public List<WasteCategory> TopWasteCategories { get; set; } = new();

    // Página 4 - Detalhamento
    public List<CostRecommendation> DetailedRecommendations { get; set; } = new();

    // Página 5 - Governança
    public GovernanceReport GovernanceReport { get; set; } = new();
}

/// <summary>
/// Resumo executivo para primeira página do PDF
/// </summary>
public class ExecutiveSummary
{
    public string AnalysisPeriod { get; set; } = string.Empty;
    public string AnalyzedScope { get; set; } = string.Empty;
    public int TotalResourcesAnalyzed { get; set; }
    public int TotalRecommendations { get; set; }
    public decimal EstimatedMonthlySavings { get; set; }
    public decimal EstimatedAnnualSavings => EstimatedMonthlySavings * 12;
    public string Currency { get; set; } = "BRL";
    public List<TopOpportunity> TopThreeOpportunities { get; set; } = new();
}

/// <summary>
/// Categoria de desperdício para organizar recomendações
/// </summary>
public class WasteCategory
{
    public string CategoryName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int AffectedResources { get; set; }
    public decimal EstimatedMonthlySavings { get; set; }
    public List<string> CommonExamples { get; set; } = new();
    public string Priority { get; set; } = "Medium";
}

/// <summary>
/// Relatório de governança detalhado
/// </summary>
public class GovernanceReport
{
    public int TotalResourcesEvaluated { get; set; }
    public int ResourcesWithoutTags { get; set; }
    public int ResourcesWithInconsistentTags { get; set; }
    public decimal TaggingCoveragePercentage { get; set; }

    public List<TagComplianceBySubscription> TaggingBySubscription { get; set; } = new();
    public List<TagIssue> CriticalTagIssues { get; set; } = new();
    public List<string> RecommendedActions { get; set; } = new();

    // Observações para relatório
    public List<string> GovernanceObservations { get; set; } = new();
}

/// <summary>
/// Conformidade de tags por subscription
/// </summary>
public class TagComplianceBySubscription
{
    public string SubscriptionName { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public int TotalResources { get; set; }
    public int TaggedResources { get; set; }
    public decimal CompliancePercentage => TotalResources > 0 ? 
        Math.Round((decimal)TaggedResources / TotalResources * 100, 2) : 0;
}

/// <summary>
/// Informações de subscription para relatórios
/// </summary>
public class SubscriptionInfo
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;
    public int ResourceCount { get; set; }
    public int RecommendationCount { get; set; }
    public decimal EstimatedMonthlySavings { get; set; }
    public string ManagementGroup { get; set; } = string.Empty;
}

/// <summary>
/// Oportunidade de economia top para relatório executivo
/// </summary>
public class TopOpportunity
{
    public string ResourceName { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public decimal EstimatedMonthlySavings { get; set; }
    public string Priority { get; set; } = "Medium";
}

/// <summary>
/// Problema de tag identificado na governança
/// </summary>
public class TagIssue
{
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;
    public string IssueType { get; set; } = string.Empty; // "Missing", "Invalid", "Inconsistent"
    public string TagName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}