using System.ComponentModel.DataAnnotations;

namespace Gvdasa.GVmodeloexemploapi.Modelos.FinOps;

public class ResourceUsage
{
    [Key]
    public Guid Id { get; init; } = Guid.NewGuid();
    
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public DateTime MeasurementDate { get; set; }
    
    // Métricas básicas
    public double CpuPercentage { get; set; }
    public double MemoryPercentage { get; set; }
    public double DiskIOPercentage { get; set; }
    public double NetworkInBytes { get; set; }
    public double NetworkOutBytes { get; set; }
    
    // App Service específico
    public int HttpRequests { get; set; }
    public double ResponseTime { get; set; }
    
    // SQL Database específico
    public double DtuPercentage { get; set; }
    public double StoragePercentage { get; set; }
    
    // Flags de estado
    public bool IsRunning { get; set; }
    public DateTime LastStartTime { get; set; }
    public DateTime LastStopTime { get; set; }
    public int DaysInactive { get; set; }
    
    public Dictionary<string, object> CustomMetrics { get; set; } = new();
}