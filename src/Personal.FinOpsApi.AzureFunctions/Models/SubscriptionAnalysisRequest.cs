namespace Personal.FinOpsApi.AzureFunctions.Models;

/// <summary>
///  Modelo da mensagem de análise de subscription (compartilhado entre Functions)
/// Usado pelas queue functions para processar análises de subscription
/// </summary>
public class SubscriptionAnalysisRequest
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string AnalysisType { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public bool IsProduction { get; set; }
    public int? RetryCount { get; set; }
}