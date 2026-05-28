using System.Text.Json.Serialization;

namespace Personal.FinOpsApi.AzureFunctions.Models;

/// <summary>
///  LOG ANALYTICS: Entrada individual de recomendação FinOps para dashboards
///  Otimizado para consultas KQL e dashboards (Azure Workbook/Grafana)
/// 
/// Exemplos de KQL que isso permite:
/// - FinOpsRecommendations_CL | summarize sum(EstimatedMonthlySavings_d) by RecommendationType_s
/// - FinOpsRecommendations_CL | top 20 by EstimatedMonthlySavings_d desc
/// - FinOpsRecommendations_CL | where Priority_s == "High" | count
/// </summary>
public class FinOpsLogEntry
{
    /// <summary>
    ///  ID único da análise (para agrupar recomendações da mesma execução)
    /// </summary>
    [JsonPropertyName("analysisId")]
    public string AnalysisId { get; set; } = string.Empty;

    /// <summary>
    ///  Timestamp da análise (será mapeado para TimeGenerated_t no Log Analytics)
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>
    ///  Subscription ID da Azure
    /// </summary>
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>
    ///  ID completo do recurso Azure (/subscriptions/.../resourceGroups/.../providers/...)
    /// </summary>
    [JsonPropertyName("resourceId")]
    public string ResourceId { get; set; } = string.Empty;

    /// <summary>
    ///  Nome do Resource Group (extraído do resourceId para facilitar queries)
    /// </summary>
    [JsonPropertyName("resourceGroupName")]
    public string ResourceGroupName { get; set; } = string.Empty;

    /// <summary>
    ///  Nome do recurso (extraído do resourceId)
    /// </summary>
    [JsonPropertyName("resourceName")]
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>
    ///  Tipo do recurso (Microsoft.Compute/disks, Microsoft.Network/publicIPAddresses, etc.)
    /// </summary>
    [JsonPropertyName("resourceType")]
    public string ResourceType { get; set; } = string.Empty;

    /// <summary>
    ///  Tipo da recomendação FinOps
    /// Valores: "OrphanedDisk", "OrphanedPublicIP", "IdleVM", "UnderutilizedStorage", "OverprovisionedAppService"
    /// </summary>
    [JsonPropertyName("recommendationType")]
    public string RecommendationType { get; set; } = string.Empty;

    /// <summary>
    ///  Categoria da recomendação (para agrupamento)
    /// Valores: "Storage", "Compute", "Network", "AppService"
    /// </summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    ///  Prioridade da recomendação
    /// Valores: "High", "Medium", "Low"
    /// </summary>
    [JsonPropertyName("priority")]
    public string Priority { get; set; } = string.Empty;

    /// <summary>
    ///  Economia estimada mensal em USD
    /// </summary>
    [JsonPropertyName("estimatedMonthlySavings")]
    public decimal EstimatedMonthlySavings { get; set; }

    /// <summary>
    ///  Ação recomendada
    /// Valores: "Delete", "Resize", "Shutdown", "Optimize"
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    /// <summary>
    ///  Descrição detalhada da recomendação
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    ///  Região do Azure onde está o recurso
    /// </summary>
    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    /// <summary>
    ///  Tags do recurso (formato JSON string para facilitar KQL)
    /// Exemplo: '{"Environment":"Production","Owner":"TeamA"}'
    /// </summary>
    [JsonPropertyName("resourceTags")]
    public string ResourceTags { get; set; } = string.Empty;

    /// <summary>
    ///  Tipo da análise que gerou esta recomendação
    /// Valores: "daily", "bi-weekly", "manual", "full"
    /// </summary>
    [JsonPropertyName("analysisType")]
    public string AnalysisType { get; set; } = string.Empty;

    /// <summary>
    ///  Métricas específicas do recurso (formato JSON string)
    /// Exemplo para Storage: '{"sizeGB":1024,"utilizationPercent":15.5}'
    /// Exemplo para VM: '{"cpuPercent":5.2,"memoryPercent":12.3}'
    /// </summary>
    [JsonPropertyName("metrics")]
    public string Metrics { get; set; } = string.Empty;

    /// <summary>
    ///  Confidence score da recomendação (0-100)
    /// 100 = certeza absoluta, <50 = revisar manualmente
    /// </summary>
    [JsonPropertyName("confidenceScore")]
    public int ConfidenceScore { get; set; }

    /// <summary>
    ///  Custo atual estimado mensal do recurso em USD
    /// </summary>
    [JsonPropertyName("currentMonthlyCost")]
    public decimal CurrentMonthlyCost { get; set; }
}