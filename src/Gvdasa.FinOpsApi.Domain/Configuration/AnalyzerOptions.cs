namespace Gvdasa.GVmodeloexemploapi.Domain.Configuration;

public class AnalyzerOptions
{
    public const string SectionName = "FinOps:Analyzer";
    
    // Configurações gerais
    public bool EnableVmAnalysis { get; set; } = true;
    public bool EnableDiskAnalysis { get; set; } = true;
    public bool EnableAppServiceAnalysis { get; set; } = true;
    public bool EnableSqlAnalysis { get; set; } = true;
    
    // Configurações de threshold globais
    public decimal MinimumCostToAnalyze { get; set; } = 50m;
    public double LowCpuThreshold { get; set; } = 5.0;
    public int DaysInactiveThreshold { get; set; } = 7;
    
    // Configurações específicas de VM
    public VmAnalyzerOptions Vm { get; set; } = new();
    
    // Configurações específicas de Disk
    public DiskAnalyzerOptions Disk { get; set; } = new();
    
    // Configurações específicas de App Service
    public AppServiceAnalyzerOptions AppService { get; set; } = new();
    
    // Configurações específicas de SQL
    public SqlAnalyzerOptions Sql { get; set; } = new();
    
    // Configurações de escopo
    public ScopeOptions Scope { get; set; } = new();
}

public class VmAnalyzerOptions
{
    public double LowCpuThreshold { get; set; } = 5.0;
    public double VeryLowCpuThreshold { get; set; } = 2.0;
    public decimal MinimumCostToAnalyze { get; set; } = 100m;
    public int InactiveDaysThreshold { get; set; } = 7;
    public bool EnableReservedInstanceRecommendations { get; set; } = true;
}

public class DiskAnalyzerOptions
{
    public decimal MinimumCostToAnalyze { get; set; } = 50m;
    public bool AnalyzePremiumDiskUsage { get; set; } = true;
    public double LowDiskIOThreshold { get; set; } = 20.0;
}

public class AppServiceAnalyzerOptions
{
    public int LowRequestThreshold { get; set; } = 100;
    public double LowCpuThreshold { get; set; } = 10.0;
    public decimal MinimumCostToAnalyze { get; set; } = 80m;
    public bool EnableFunctionAppRecommendations { get; set; } = true;
}

public class SqlAnalyzerOptions
{
    public double LowDtuThreshold { get; set; } = 20.0;
    public double LowStorageThreshold { get; set; } = 50.0;
    public decimal MinimumCostToAnalyze { get; set; } = 100m;
    public bool EnableElasticPoolRecommendations { get; set; } = true;
    public bool EnableServerlessRecommendations { get; set; } = true;
}

public class ScopeOptions
{
    public string[]? SubscriptionIds { get; set; }
    public string[]? ResourceGroupNames { get; set; }
    public string[]? ExcludeSubscriptionIds { get; set; }
    public string[]? TagFilters { get; set; }
    public bool UseManagementGroupScope { get; set; } = true;
    public string? ManagementGroupId { get; set; }
}