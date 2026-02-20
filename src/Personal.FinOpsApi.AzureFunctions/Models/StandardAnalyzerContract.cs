using System.Text.Json.Serialization;

namespace Personal.FinOpsApi.AzureFunctions.Models;

/// <summary>
/// CONTRATO PADRÃO v1.0 - Todos os analyzers DEVEM seguir esta estrutura
/// </summary>
public class StandardAnalyzerResult
{
    /// <summary>
    /// Versão do schema para compatibilidade futura
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0";

    /// <summary>
    /// ID único da execução da análise
    /// </summary>
    [JsonPropertyName("analysisId")]
    public string AnalysisId { get; set; } = string.Empty;

    /// <summary>
    /// Nome do analyzer que executou
    /// </summary>
    [JsonPropertyName("analyzer")]
    public string Analyzer { get; set; } = string.Empty;

    /// <summary>
    /// Escopo da análise: subscription, managementGroup, tenant
    /// </summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "subscription";

    /// <summary>
    /// ID da subscription analisada
    /// </summary>
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp da execução
    /// </summary>
    [JsonPropertyName("executedAt")]
    public DateTime ExecutedAt { get; set; }

    /// <summary>
    /// Período de dias analisados
    /// </summary>
    [JsonPropertyName("analysisPeriodDays")]
    public int AnalysisPeriodDays { get; set; }

    /// <summary>
    /// Se é execução de teste ou produção
    /// </summary>
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }

    /// <summary>
    /// Lista de findings encontrados
    /// </summary>
    [JsonPropertyName("findings")]
    public List<StandardFinding> Findings { get; set; } = new();

    /// <summary>
    /// Metadados da execução
    /// </summary>
    [JsonPropertyName("executionMetadata")]
    public Dictionary<string, object> ExecutionMetadata { get; set; } = new();
}

/// <summary>
/// FINDING PADRÃO - Cada recomendação de custo deve seguir essa estrutura
/// </summary>
public class StandardFinding
{
    /// <summary>
    /// Tipo da recomendação (obrigatório)
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// ID completo do recurso Azure (obrigatório)
    /// </summary>
    [JsonPropertyName("resourceId")]
    public string ResourceId { get; set; } = string.Empty;

    /// <summary>
    /// Nome do recurso (obrigatório)
    /// </summary>
    [JsonPropertyName("resourceName")]
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>
    /// Tipo do recurso Azure (obrigatório)
    /// </summary>
    [JsonPropertyName("resourceType")]
    public string ResourceType { get; set; } = string.Empty;

    /// <summary>
    /// Resource Group (obrigatório)
    /// </summary>
    [JsonPropertyName("resourceGroup")]
    public string ResourceGroup { get; set; } = string.Empty;

    /// <summary>
    /// Subscription ID (obrigatório)
    /// </summary>
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>
    /// Localização do recurso
    /// </summary>
    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    /// <summary>
    /// Economia potencial mensal em valor (obrigatório)
    /// </summary>
    [JsonPropertyName("estimatedMonthlySavings")]
    public decimal EstimatedMonthlySavings { get; set; }

    /// <summary>
    /// Custo atual estimado mensal
    /// </summary>
    [JsonPropertyName("estimatedMonthlyCost")]
    public decimal EstimatedMonthlyCost { get; set; }

    /// <summary>
    /// Moeda (BRL, USD, EUR)
    /// </summary>
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "BRL";

    /// <summary>
    /// Prioridade: Low, Medium, High (obrigatório)
    /// </summary>
    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "Medium";

    /// <summary>
    /// Nível de confiança (0.0 a 1.0)
    /// </summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 0.6;

    /// <summary>
    /// Descrição do problema (obrigatório)
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Ação recomendada
    /// </summary>
    [JsonPropertyName("recommendation")]
    public string Recommendation { get; set; } = string.Empty;

    /// <summary>
    /// Tags do recurso
    /// </summary>
    [JsonPropertyName("tags")]
    public Dictionary<string, string> Tags { get; set; } = new();

    /// <summary>
    /// Metadados específicos do analyzer
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Enums para padronização
/// </summary>
public static class FindingTypes
{
    public const string UNDER_UTILIZED_STORAGE_ACCOUNT = "UnderUtilizedStorageAccount";
    public const string UNATTACHED_DISK = "UnattachedDisk";
    public const string UNUSED_PUBLIC_IP = "UnusedPublicIP";
    public const string IDLE_VM = "IdleVirtualMachine";
    public const string UNDERUTILIZED_APP_SERVICE = "UnderUtilizedAppService";
}

public static class FindingPriorities
{
    public const string LOW = "Low";
    public const string MEDIUM = "Medium";
    public const string HIGH = "High";
}

public static class AnalyzerNames
{
    public const string STORAGE_ACCOUNT_ANALYZER = "StorageAccountAnalyzer";
    public const string UNATTACHED_DISK_ANALYZER = "UnattachedDiskAnalyzer";
    public const string UNUSED_PUBLIC_IP_ANALYZER = "UnusedPublicIpAnalyzer";
    public const string IDLE_VM_ANALYZER = "IdleVmAnalyzer";
    public const string APP_SERVICE_ANALYZER = "AppServiceAnalyzer";
}