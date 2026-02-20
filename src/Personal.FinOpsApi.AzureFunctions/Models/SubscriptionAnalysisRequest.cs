namespace Personal.FinOpsApi.AzureFunctions.Models;

public class SubscriptionAnalysisRequest
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string AnalysisType { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public int? RetryCount { get; set; }
}
