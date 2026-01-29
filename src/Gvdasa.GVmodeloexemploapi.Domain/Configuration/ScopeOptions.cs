namespace Gvdasa.GVmodeloexemploapi.Domain.Configuration;

/// <summary>
/// Configurações de escopo para análise FinOps
/// </summary>
public class ScopeOptions
{
    public const string SectionName = "FinOps:Scope";

    /// <summary>
    /// Modo de escopo: ManagementGroup, Subscription, ResourceGroup
    /// </summary>
    public string Mode { get; set; } = "ManagementGroup";

    /// <summary>
    /// ID do Management Group (quando Mode = ManagementGroup)
    /// </summary>
    public string? ManagementGroupId { get; set; } = "mg-gvdasa";

    /// <summary>
    /// Lista específica de subscriptions para incluir (opcional)
    /// </summary>
    public List<string> IncludeSubscriptions { get; set; } = new();

    /// <summary>
    /// Lista de subscriptions para excluir (opcional)
    /// </summary>
    public List<string> ExcludeSubscriptions { get; set; } = new();

    /// <summary>
    /// Validar configuração
    /// </summary>
    public bool IsValid()
    {
        return Mode switch
        {
            "ManagementGroup" => !string.IsNullOrEmpty(ManagementGroupId),
            "Subscription" => IncludeSubscriptions.Any(),
            _ => false
        };
    }
}