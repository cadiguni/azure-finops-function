using Gvdasa.FinOpsApi.AzureFunctions.Analyzers;
using Gvdasa.FinOpsApi.AzureFunctions.Models;

namespace Gvdasa.FinOpsApi.AzureFunctions.Application;

public class CostAnalysisOrchestrator
{
    private readonly UnattachedDiskAnalyzer _diskAnalyzer;

    public CostAnalysisOrchestrator(UnattachedDiskAnalyzer diskAnalyzer)
    {
        _diskAnalyzer = diskAnalyzer;
    }

    /// <summary>
    /// Executa análise completa baseada na requisição
    /// </summary>
    public async Task<CostAnalysisResult> ExecuteAnalysisAsync(CostAnalysisRequest request)
    {
        var result = new CostAnalysisResult
        {
            Scope = request.Scope,
            SubscriptionId = request.SubscriptionId,
            ManagementGroupId = request.ManagementGroupId,
            AnalysisPeriodDays = request.AnalysisPeriodDays,
            DryRun = request.DryRun
        };

        var allRecommendations = new List<CostRecommendation>();

        try
        {
            // Validar request
            ValidateRequest(request);

            // Executar análises baseadas na configuração Include
            if (request.Include.UnattachedDisks)
            {
                var diskRecommendations = await AnalyzeUnattachedDisksAsync(request);
                allRecommendations.AddRange(diskRecommendations);
            }

            // Futuras análises virão aqui:
            // if (request.Include.Vms) { ... }
            // if (request.Include.AppServices) { ... }
            // if (request.Include.SqlDatabases) { ... }

            result.Recommendations = allRecommendations;
            result.Summary = BuildSummary(allRecommendations);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro na análise de custo: {ex.Message}");
            // Em caso de erro, retorna resultado vazio mas válido
        }

        return result;
    }

    /// <summary>
    /// Analisa discos não anexados
    /// </summary>
    private async Task<List<CostRecommendation>> AnalyzeUnattachedDisksAsync(CostAnalysisRequest request)
    {
        var recommendations = new List<CostRecommendation>();

        try
        {
            if (request.Scope.Equals("subscription", StringComparison.OrdinalIgnoreCase) && 
                !string.IsNullOrEmpty(request.SubscriptionId))
            {
                var diskRecs = await _diskAnalyzer.AnalyzeSubscriptionAsync(request.SubscriptionId);
                recommendations.AddRange(diskRecs);
            }
            else if (request.Scope.Equals("managementGroup", StringComparison.OrdinalIgnoreCase))
            {
                // TODO: Implementar análise por Management Group
                // Requer listagem de subscriptions no MG
                Console.WriteLine("Análise por Management Group ainda não implementada");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro ao analisar discos não anexados: {ex.Message}");
        }

        return recommendations;
    }

    /// <summary>
    /// Valida se a requisição está correta
    /// </summary>
    private void ValidateRequest(CostAnalysisRequest request)
    {
        if (request.Scope.Equals("subscription", StringComparison.OrdinalIgnoreCase) && 
            string.IsNullOrEmpty(request.SubscriptionId))
        {
            throw new ArgumentException("SubscriptionId é obrigatório quando scope = 'subscription'");
        }

        if (request.Scope.Equals("managementGroup", StringComparison.OrdinalIgnoreCase) && 
            string.IsNullOrEmpty(request.ManagementGroupId))
        {
            throw new ArgumentException("ManagementGroupId é obrigatório quando scope = 'managementGroup'");
        }

        if (request.AnalysisPeriodDays < 1 || request.AnalysisPeriodDays > 365)
        {
            throw new ArgumentException("AnalysisPeriodDays deve estar entre 1 e 365");
        }
    }

    /// <summary>
    /// Constrói resumo consolidado
    /// </summary>
    private CostAnalysisSummary BuildSummary(List<CostRecommendation> recommendations)
    {
        var summary = new CostAnalysisSummary
        {
            TotalResourcesAnalyzed = recommendations.Count,
            TotalRecommendations = recommendations.Count,
            TotalEstimatedMonthlySavings = recommendations.Sum(r => r.EstimatedMonthlySavings)
        };

        // Breakdown por tipo
        var typeGroups = recommendations.GroupBy(r => r.Type);
        foreach (var group in typeGroups)
        {
            summary.ByType[group.Key] = new RecommendationTypeSummary
            {
                Count = group.Count(),
                EstimatedMonthlySavings = group.Sum(r => r.EstimatedMonthlySavings)
            };
        }

        return summary;
    }
}