namespace Gvdasa.FinOpsApi.Domain.Configuration;

/// <summary>
/// Configurações de classificação de ambiente para FinOps
/// </summary>
public class EnvironmentClassificationOptions
{
    public const string SectionName = "FinOps:EnvironmentClassification";

    /// <summary>
    /// Management Groups considerados de produção
    /// </summary>
    public List<string> ProductionManagementGroups { get; set; } = new() { "Setores" };

    /// <summary>
    /// Management Groups considerados de não-produção (MPN)
    /// </summary>
    public List<string> NonProductionManagementGroups { get; set; } = new() { "VisualStudio" };

    /// <summary>
    /// Verificar se um Management Group é de produção
    /// </summary>
    /// <param name="managementGroupName">Nome do Management Group</param>
    /// <returns>True se for ambiente de produção</returns>
    public bool IsProductionEnvironment(string managementGroupName)
    {
        return ProductionManagementGroups.Contains(managementGroupName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verificar se um Management Group é de não-produção
    /// </summary>
    /// <param name="managementGroupName">Nome do Management Group</param>
    /// <returns>True se for ambiente de não-produção</returns>
    public bool IsNonProductionEnvironment(string managementGroupName)
    {
        return NonProductionManagementGroups.Contains(managementGroupName, StringComparer.OrdinalIgnoreCase);
    }
}