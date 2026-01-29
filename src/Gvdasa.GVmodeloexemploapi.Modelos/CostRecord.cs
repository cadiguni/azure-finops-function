using System.ComponentModel.DataAnnotations;

namespace Gvdasa.GVmodeloexemploapi.Modelos.FinOps;

public class CostRecord
{
    [Key]
    public Guid Id { get; init; } = Guid.NewGuid();
    
    public string SubscriptionId { get; set; } = string.Empty;
    public string ResourceGroupName { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public decimal MonthlyCost { get; set; }
    public decimal DailyCost { get; set; }
    public DateTime AnalysisDate { get; set; }
    public DateTime CostPeriodStart { get; set; }
    public DateTime CostPeriodEnd { get; set; }
    public string Currency { get; set; } = "BRL";
    public Dictionary<string, object> AdditionalProperties { get; set; } = new();
}