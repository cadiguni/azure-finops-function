using System.Text.Json.Serialization;

namespace Personal.FinOpsApi.AzureFunctions.Models;

/// <summary>
/// Modelo simplificado para agregação de dados de custo
/// Contrato padrão que todos os analyzers devem gerar
/// </summary>
public class CostFinding
{
    [JsonPropertyName("resourceId")]
    public string ResourceId { get; set; } = string.Empty;

    [JsonPropertyName("resourceType")]
    public string ResourceType { get; set; } = string.Empty;

    [JsonPropertyName("resourceName")]
    public string ResourceName { get; set; } = string.Empty;

    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    [JsonPropertyName("resourceGroup")]
    public string ResourceGroup { get; set; } = string.Empty;

    [JsonPropertyName("estimatedMonthlyCost")]
    public decimal EstimatedMonthlyCost { get; set; }

    [JsonPropertyName("dailyCost")]
    public decimal DailyCost { get; set; }

    [JsonPropertyName("potentialSavings")]
    public decimal PotentialSavings { get; set; }

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "Medium";

    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "Medium";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}