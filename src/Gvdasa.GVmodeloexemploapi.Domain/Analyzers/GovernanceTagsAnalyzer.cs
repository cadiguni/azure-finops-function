using Gvdasa.GVmodeloexemploapi.Modelos.FinOps;

namespace Gvdasa.GVmodeloexemploapi.Domain.Analyzers;

/// <summary>
/// Analyzer para verificar compliance de tags obrigatórias de governança
/// </summary>
public class GovernanceTagsAnalyzer : BaseAnalyzer
{
    private readonly string[] _requiredTags = { "owner", "environment", "cost-center" };

    public GovernanceTagsAnalyzer(ILogger<GovernanceTagsAnalyzer> logger) : base(logger)
    {
    }

    public override async Task<List<OptimizationFinding>> AnalyzeAsync(List<ResourceUsage> resources)
    {
        var findings = new List<OptimizationFinding>();

        foreach (var resource in resources)
        {
            var missingTags = GetMissingTags(resource);
            
            if (missingTags.Any())
            {
                findings.Add(new OptimizationFinding
                {
                    Id = Guid.NewGuid(),
                    ResourceId = resource.ResourceId,
                    ResourceName = resource.ResourceName,
                    ResourceType = resource.ResourceType,
                    SubscriptionId = resource.SubscriptionId,
                    ResourceGroup = resource.ResourceGroup,
                    Location = resource.Location,
                    Category = "Governance",
                    Severity = GetSeverityByMissingTags(missingTags.Count),
                    Title = $"Tags obrigatórias ausentes: {string.Join(", ", missingTags)}",
                    Description = $"O recurso {resource.ResourceName} não possui as seguintes tags obrigatórias de governança: {string.Join(", ", missingTags)}. " +
                                $"Isso dificulta a rastreabilidade de custos e responsabilidades.",
                    Recommendation = $"Adicione as tags obrigatórias: {string.Join(", ", missingTags.Select(t => $"{t}=<valor>"))}",
                    PotentialMonthlySaving = 0, // Tags não geram economia direta, mas melhoram governança
                    Impact = "Governança e Compliance",
                    AnalyzedAt = DateTime.UtcNow,
                    Tags = resource.Tags,
                    Metrics = new Dictionary<string, object>
                    {
                        ["MissingTagCount"] = missingTags.Count,
                        ["TotalTagCount"] = resource.Tags?.Count ?? 0,
                        ["MissingTags"] = missingTags
                    }
                });
            }
        }

        Logger.LogInformation("Análise de governança concluída: {ResourceCount} recursos analisados, {FindingCount} não-conformidades encontradas", 
            resources.Count, findings.Count);

        return findings;
    }

    private List<string> GetMissingTags(ResourceUsage resource)
    {
        var resourceTags = resource.Tags?.Keys?.Select(k => k.ToLowerInvariant()) ?? Enumerable.Empty<string>();
        return _requiredTags.Where(tag => !resourceTags.Contains(tag.ToLowerInvariant())).ToList();
    }

    private string GetSeverityByMissingTags(int missingTagCount)
    {
        return missingTagCount switch
        {
            1 => "Medium",
            2 => "High", 
            >= 3 => "Critical",
            _ => "Low"
        };
    }
}