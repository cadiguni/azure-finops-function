using Gvdasa.FinOpsApi.Modelos.FinOps;
using Gvdasa.FinOpsApi.Domain.Configuration;
using Microsoft.Extensions.Options;

namespace Gvdasa.FinOpsApi.Domain.Analyzers;

/// <summary>
/// Analyzer para classificação de ambiente baseado em tags e Management Groups
/// </summary>
public class EnvironmentClassificationAnalyzer : BaseAnalyzer
{
    private readonly EnvironmentClassificationOptions _environmentOptions;
    private readonly BehaviorOptions _behaviorOptions;

    public EnvironmentClassificationAnalyzer(
        ILogger<EnvironmentClassificationAnalyzer> logger,
        IOptions<EnvironmentClassificationOptions> environmentOptions,
        IOptions<BehaviorOptions> behaviorOptions) : base(logger)
    {
        _environmentOptions = environmentOptions.Value;
        _behaviorOptions = behaviorOptions.Value;
    }

    /// <summary>
    /// Classificar recurso como produção ou não-produção
    /// </summary>
    /// <param name="resource">Recurso a ser classificado</param>
    /// <returns>True se é produção</returns>
    public bool IsProductionResource(ResourceUsage resource)
    {
        // 1. Prioridade: Tag 'environment' 
        if (resource.Tags?.ContainsKey("environment") == true)
        {
            var envTag = resource.Tags["environment"].ToLowerInvariant();
            return envTag == "prod" || envTag == "production";
        }

        // 2. Fallback: Management Group (se disponível no ResourceUsage)
        if (!string.IsNullOrEmpty(resource.ManagementGroupName))
        {
            return _environmentOptions.IsProductionEnvironment(resource.ManagementGroupName);
        }

        // 3. Fallback final: Assumir não-produção por segurança
        return false;
    }

    /// <summary>
    /// Obter configurações de análise baseado no recurso
    /// </summary>
    /// <param name="resource">Recurso a ser analisado</param>
    /// <returns>Opções de análise configuradas</returns>
    public AnalysisOptions GetAnalysisOptions(ResourceUsage resource)
    {
        bool isProduction = IsProductionResource(resource);
        return _behaviorOptions.GetAnalysisOptions(isProduction);
    }

    public override async Task<List<OptimizationFinding>> AnalyzeAsync(List<ResourceUsage> resources)
    {
        var findings = new List<OptimizationFinding>();
        int productionResources = 0;
        int nonProductionResources = 0;

        foreach (var resource in resources)
        {
            if (IsProductionResource(resource))
            {
                productionResources++;
            }
            else
            {
                nonProductionResources++;
            }
        }

        Logger.LogInformation("Classificação de ambiente: {ProductionCount} recursos de produção, {NonProductionCount} recursos de não-produção",
            productionResources, nonProductionResources);

        // Opcional: Gerar finding sobre recursos não classificados
        var unclassifiedResources = resources
            .Where(r => !r.Tags?.ContainsKey("environment") == true && string.IsNullOrEmpty(r.ManagementGroupName))
            .ToList();

        if (unclassifiedResources.Any())
        {
            findings.Add(new OptimizationFinding
            {
                Id = Guid.NewGuid(),
                ResourceId = "classification-warning",
                ResourceName = "Recursos não classificados",
                ResourceType = "Environment",
                Category = "Governance",
                Severity = "Medium",
                Title = $"{unclassifiedResources.Count} recursos sem classificação de ambiente",
                Description = "Recursos encontrados sem tag 'environment' e sem Management Group identificado. " +
                            "Isso pode impactar a aplicação das políticas corretas de FinOps.",
                Recommendation = "Adicionar tag 'environment=prod|dev|hml' nos recursos ou organizar em Management Groups apropriados",
                PotentialMonthlySaving = 0,
                Impact = "Governança e Segurança",
                AnalyzedAt = DateTime.UtcNow,
                Metrics = new Dictionary<string, object>
                {
                    ["UnclassifiedResourceCount"] = unclassifiedResources.Count,
                    ["ProductionResourceCount"] = productionResources,
                    ["NonProductionResourceCount"] = nonProductionResources
                }
            });
        }

        return findings;
    }
}