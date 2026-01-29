namespace Gvdasa.FinOpsApi.Domain.Configuration;

/// <summary>
/// Configurações de comportamento baseado em ambiente
/// </summary>
public class BehaviorOptions
{
    public const string SectionName = "FinOps:Behavior";

    /// <summary>
    /// Executar sempre em modo dry-run em produção
    /// </summary>
    public bool DryRunInProduction { get; set; } = true;

    /// <summary>
    /// Permitir automação em produção (desabilitado por segurança)
    /// </summary>
    public bool AllowAutomationInProduction { get; set; } = false;

    /// <summary>
    /// Configurar opções baseado no ambiente
    /// </summary>
    /// <param name="isProduction">Se é ambiente de produção</param>
    /// <returns>Opções de análise configuradas</returns>
    public AnalysisOptions GetAnalysisOptions(bool isProduction)
    {
        var options = new AnalysisOptions();

        if (isProduction)
        {
            options.DryRun = DryRunInProduction;
            options.AllowAutomation = AllowAutomationInProduction;
            options.ReadOnly = true;
            options.ReportOnly = true;
        }
        else
        {
            options.DryRun = false;
            options.AllowAutomation = true;
            options.ReadOnly = false;
            options.ReportOnly = false;
        }

        return options;
    }
}

/// <summary>
/// Opções de análise baseadas no ambiente
/// </summary>
public class AnalysisOptions
{
    /// <summary>
    /// Modo dry-run (apenas análise, sem alterações)
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// Permitir automação
    /// </summary>
    public bool AllowAutomation { get; set; } = false;

    /// <summary>
    /// Somente leitura
    /// </summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>
    /// Apenas relatórios
    /// </summary>
    public bool ReportOnly { get; set; } = true;
}