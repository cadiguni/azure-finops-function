using System.ComponentModel.DataAnnotations;

namespace Gvdasa.GVmodeloexemploapi.Modelos.FinOps;

public class OptimizationFinding
{
    [Key]
    public Guid Id { get; init; } = Guid.NewGuid();
    
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string ResourceGroupName { get; set; } = string.Empty;
    
    public OptimizationType Type { get; set; }
    public OptimizationSeverity Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    
    public decimal CurrentMonthlyCost { get; set; }
    public decimal EstimatedMonthlySaving { get; set; }
    public decimal SavingPercentage { get; set; }
    
    public DateTime DiscoveredDate { get; set; }
    public DateTime AnalyzedDate { get; set; }
    public bool IsActionable { get; set; }
    public string ActionSteps { get; set; } = string.Empty;
    
    public Dictionary<string, object> Evidence { get; set; } = new();
    public List<string> Tags { get; set; } = new();
}

public enum OptimizationType
{
    VM_IDLE,
    VM_OVERSIZED,
    DISK_UNATTACHED,
    IP_UNASSIGNED,
    APP_SERVICE_IDLE,
    SQL_OVERSIZED,
    STORAGE_UNUSED,
    RESERVED_INSTANCE_OPPORTUNITY
}

public enum OptimizationSeverity
{
    LOW,
    MEDIUM,
    HIGH,
    CRITICAL
}