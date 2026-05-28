using System.ComponentModel.DataAnnotations;

namespace Personal.FinOpsApi.AzureFunctions.Models;

public class CostAnalysisRequest
{
    /// <summary>
    /// Escopo da análise: "subscription", "managementGroup", "resourceGroup"
    /// </summary>
    [Required]
    public string Scope { get; set; } = "subscription";
    
    /// <summary>
    /// ID da subscription (obrigatório se scope = "subscription")
    /// </summary>
    public string? SubscriptionId { get; set; }
    
    /// <summary>
    /// ID do Management Group (obrigatório se scope = "managementGroup")
    /// </summary>
    public string? ManagementGroupId { get; set; }
    
    /// <summary>
    /// Período de análise em dias (padrão: 30 dias)
    /// </summary>
    [Range(1, 365)]
    public int AnalysisPeriodDays { get; set; } = 30;
    
    /// <summary>
    /// Se true, não executa ações, apenas simula
    /// </summary>
    public bool DryRun { get; set; } = true; //  Default seguro: sempre dry-run se não especificado
    
    /// <summary>
    /// Configuração do que incluir na análise
    /// </summary>
    public AnalysisIncludeOptions AnalysisOptions { get; set; } = new(); //  NUNCA null!
}

public class AnalysisIncludeOptions
{
    /// <summary>
    /// Analisar VMs subutilizadas
    /// </summary>
    public bool Vms { get; set; } = false;
    
    /// <summary>
    /// Analisar discos não anexados
    /// </summary>
    public bool UnattachedDisks { get; set; } = true;
    
    /// <summary>
    /// Analisar Storage Accounts subutilizados
    /// </summary>
    public bool StorageAccounts { get; set; } = true;
    
    /// <summary>
    /// Analisar IPs públicos não utilizados
    /// </summary>
    public bool UnusedPublicIps { get; set; } = true;
    
    /// <summary>
    /// Analisar VMs ligadas mas ociosas (maior impacto FinOps)
    /// </summary>
    public bool IdleVms { get; set; } = true;
    
    /// <summary>
    /// Analisar App Services subutilizados
    /// </summary>
    public bool AppServices { get; set; } = true;
    
    /// <summary>
    /// Analisar SQL Databases subutilizados
    /// </summary>
    public bool SqlDatabases { get; set; } = false;
}