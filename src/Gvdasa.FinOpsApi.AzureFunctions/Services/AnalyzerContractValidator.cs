using Gvdasa.FinOpsApi.AzureFunctions.Models;

namespace Gvdasa.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Validador para garantir que todos os analyzers seguem o contrato padrão
/// </summary>
public class AnalyzerContractValidator
{
    /// <summary>
    /// Valida se o resultado segue o contrato v1.0
    /// </summary>
    public static (bool IsValid, List<string> Errors) ValidateResult(StandardAnalyzerResult result)
    {
        var errors = new List<string>();

        // Validações obrigatórias do resultado
        if (string.IsNullOrWhiteSpace(result.AnalysisId))
            errors.Add("AnalysisId é obrigatório");

        if (string.IsNullOrWhiteSpace(result.Analyzer))
            errors.Add("Analyzer é obrigatório");

        if (string.IsNullOrWhiteSpace(result.SubscriptionId))
            errors.Add("SubscriptionId é obrigatório");

        if (result.ExecutedAt == default)
            errors.Add("ExecutedAt deve ser informado");

        if (result.Findings == null)
            errors.Add("Findings não pode ser null");
        else
        {
            // Validar cada finding
            for (int i = 0; i < result.Findings.Count; i++)
            {
                var findingErrors = ValidateFinding(result.Findings[i], i);
                errors.AddRange(findingErrors);
            }
        }

        return (errors.Count == 0, errors);
    }

    /// <summary>
    /// Valida um finding individual
    /// </summary>
    private static List<string> ValidateFinding(StandardFinding finding, int index)
    {
        var errors = new List<string>();
        var prefix = $"Finding[{index}]";

        // Campos obrigatórios
        if (string.IsNullOrWhiteSpace(finding.Type))
            errors.Add($"{prefix}: Type é obrigatório");

        if (string.IsNullOrWhiteSpace(finding.ResourceId))
            errors.Add($"{prefix}: ResourceId é obrigatório");

        if (string.IsNullOrWhiteSpace(finding.ResourceName))
            errors.Add($"{prefix}: ResourceName é obrigatório");

        if (string.IsNullOrWhiteSpace(finding.ResourceType))
            errors.Add($"{prefix}: ResourceType é obrigatório");

        if (string.IsNullOrWhiteSpace(finding.ResourceGroup))
            errors.Add($"{prefix}: ResourceGroup é obrigatório");

        if (string.IsNullOrWhiteSpace(finding.SubscriptionId))
            errors.Add($"{prefix}: SubscriptionId é obrigatório");

        if (finding.EstimatedMonthlySavings < 0)
            errors.Add($"{prefix}: EstimatedMonthlySavings deve ser >= 0");

        if (string.IsNullOrWhiteSpace(finding.Priority))
            errors.Add($"{prefix}: Priority é obrigatório");

        if (string.IsNullOrWhiteSpace(finding.Description))
            errors.Add($"{prefix}: Description é obrigatório");

        // Validações de formato
        if (!string.IsNullOrWhiteSpace(finding.Priority) && 
            finding.Priority != FindingPriorities.LOW && 
            finding.Priority != FindingPriorities.MEDIUM && 
            finding.Priority != FindingPriorities.HIGH)
        {
            errors.Add($"{prefix}: Priority deve ser Low, Medium ou High");
        }

        if (finding.Confidence < 0.0 || finding.Confidence > 1.0)
            errors.Add($"{prefix}: Confidence deve estar entre 0.0 e 1.0");

        return errors;
    }

    /// <summary>
    /// Gera um relatório de validação legível
    /// </summary>
    public static string GenerateValidationReport(StandardAnalyzerResult result)
    {
        var (isValid, errors) = ValidateResult(result);

        if (isValid)
        {
            return $"✅ CONTRATO VÁLIDO: {result.Analyzer} - {result.Findings.Count} findings";
        }

        var report = $"❌ CONTRATO INVÁLIDO: {result.Analyzer}\n";
        report += "Erros encontrados:\n";
        foreach (var error in errors)
        {
            report += $"  - {error}\n";
        }

        return report;
    }
}