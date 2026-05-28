using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Modelo unificado de relatório de recomendações
/// </summary>
public class RecommendationReport
{
    public DateTime AnalysisDate { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string Currency { get; set; } = "BRL";
    public ReportSummary Summary { get; set; } = new();
    public List<ManagementGroupReport> ManagementGroups { get; set; } = new();
}

public class ReportSummary  
{
    public int TotalRecommendations { get; set; }
    public decimal TotalPotentialSavings { get; set; }
    public Dictionary<string, int> ActionBreakdown { get; set; } = new();
    public Dictionary<string, decimal> SavingsByAction { get; set; } = new();
}

public class ManagementGroupReport
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<SubscriptionReport> Subscriptions { get; set; } = new();
    public decimal TotalSavings { get; set; }
    public int TotalRecommendations { get; set; }
}

public class SubscriptionReport
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<ResourceGroupReport> ResourceGroups { get; set; } = new();
    public decimal TotalSavings { get; set; }
    public int TotalRecommendations { get; set; }
}

public class ResourceGroupReport
{
    public string Name { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public List<ActionableRecommendation> Recommendations { get; set; } = new();
}

public class ActionableRecommendation
{
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // Excluir, Reduzir, Revisar, Monitorar
    public string Priority { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;
    public decimal PotentialSavings { get; set; }
    public decimal CurrentCost { get; set; }
    /// <summary>
    /// Custo diário médio real
    /// </summary>
    public decimal DailyCost { get; set; }
}

/// <summary>
/// Serviço central responsável por gerar relatórios de recomendações operacionais
/// </summary>
public interface IRecommendationReportService
{
    Task<RecommendationReport> GenerateReportAsync(DateTime analysisDate, string? managementGroupFilter = null, string? subscriptionFilter = null);
    Task<RecommendationReport> GenerateReportByTeamAsync(DateTime analysisDate, string? teamFilter = null);
}

/// <summary>
/// Serviço central responsável por gerar relatórios de recomendações operacionais
/// </summary>
public class RecommendationReportService : IRecommendationReportService
{
    private readonly AnalysisStorageService _storageService;
    private readonly AzureManagementGroupService _managementGroupService;
    private readonly TeamSubscriptionsService _teamSubscriptionsService;
    private readonly ILogger<RecommendationReportService> _logger;

    public RecommendationReportService(
        AnalysisStorageService storageService,
        AzureManagementGroupService managementGroupService,
        TeamSubscriptionsService teamSubscriptionsService,
        ILogger<RecommendationReportService> logger)
    {
        _storageService = storageService;
        _managementGroupService = managementGroupService;
        _teamSubscriptionsService = teamSubscriptionsService;
        _logger = logger;
    }

    /// <summary>
    /// Gera relatório consolidado de recomendações
    /// </summary>
    public async Task<RecommendationReport> GenerateReportAsync(
        DateTime analysisDate,
        string? managementGroupFilter = null,
        string? subscriptionFilter = null)
    {
        _logger.LogInformation("📊 Gerando relatório de recomendações para {date}", analysisDate.ToString("yyyy-MM-dd"));

        var recommendations = await _storageService.GetDailyAnalysisAsync(analysisDate);
        _logger.LogInformation("📄 Carregadas {count} recomendações", recommendations.Count);

        // Aplicar filtros se necessário
        if (!string.IsNullOrEmpty(managementGroupFilter))
        {
            recommendations = recommendations.Where(r => r.ManagementGroupId == managementGroupFilter).ToList();
        }

        if (!string.IsNullOrEmpty(subscriptionFilter))
        {
            recommendations = recommendations.Where(r => r.SubscriptionId == subscriptionFilter).ToList();
        }

        var report = new RecommendationReport
        {
            AnalysisDate = analysisDate,
            GeneratedAt = DateTime.UtcNow,
            ManagementGroups = await BuildManagementGroupReportsAsync(recommendations),
            Summary = BuildReportSummary(recommendations)
        };

        _logger.LogInformation("✅ Relatório gerado: {mgCount} MGs, {recCount} recomendações, R$ {savings:N2} potencial",
            report.ManagementGroups.Count, report.Summary.TotalRecommendations, report.Summary.TotalPotentialSavings);

        return report;
    }

    private async Task<List<ManagementGroupReport>> BuildManagementGroupReportsAsync(List<CostRecommendation> recommendations)
    {
        var managementGroups = await _managementGroupService.GetManagementGroupsAsync();
        var subscriptionNames = await _teamSubscriptionsService.GetSubscriptionNameMappingsAsync();
        
        var mgGroups = recommendations
            .Where(r => !string.IsNullOrEmpty(r.ManagementGroupId))
            .GroupBy(r => r.ManagementGroupId)
            .ToList();

        var mgReports = new List<ManagementGroupReport>();

        if (mgGroups.Count == 0)
        {
            // Fallback: agrupa por subscription quando não há MG mapeados
            // Cria um "MG virtual" para cada subscription
            var subscriptionGroups = recommendations
                .GroupBy(r => r.SubscriptionId)
                .ToList();

            foreach (var subGroup in subscriptionGroups)
            {
                var subRecs = subGroup.ToList();
                var subName = subscriptionNames.GetValueOrDefault(subGroup.Key, subGroup.Key);
                
                var mgReport = new ManagementGroupReport
                {
                    Id = subGroup.Key,
                    Name = $"Subscription: {subName}",
                    TotalSavings = subRecs.Sum(r => r.EstimatedMonthlySavings),
                    TotalRecommendations = subRecs.Count,
                    Subscriptions = BuildSubscriptionReports(subRecs, subscriptionNames)
                };
                mgReports.Add(mgReport);
            }
            
            return mgReports.OrderByDescending(mg => mg.TotalSavings).ToList();
        }

        foreach (var mgGroup in mgGroups)
        {
            var mgRecs = mgGroup.ToList();
            var mgReport = new ManagementGroupReport
            {
                Id = mgGroup.Key,
                Name = managementGroups.GetValueOrDefault(mgGroup.Key, mgGroup.Key),
                TotalSavings = mgRecs.Sum(r => r.EstimatedMonthlySavings),
                TotalRecommendations = mgRecs.Count,
                Subscriptions = BuildSubscriptionReports(mgRecs, subscriptionNames)
            };

            mgReports.Add(mgReport);
        }

        return mgReports.OrderByDescending(mg => mg.TotalSavings).ToList();
    }

    private List<SubscriptionReport> BuildSubscriptionReports(List<CostRecommendation> recommendations, Dictionary<string, string> subscriptionNames)
    {
        return recommendations
            .GroupBy(r => r.SubscriptionId)
            .Select(subGroup => new SubscriptionReport
            {
                Id = subGroup.Key,
                Name = subscriptionNames.GetValueOrDefault(subGroup.Key, subGroup.Key), // Usa nome do time config ou fallback para ID
                TotalSavings = subGroup.Sum(r => r.EstimatedMonthlySavings),
                TotalRecommendations = subGroup.Count(),
                ResourceGroups = BuildResourceGroupReports(subGroup.ToList())
            })
            .OrderByDescending(s => s.TotalSavings)
            .ToList();
    }

    private List<ResourceGroupReport> BuildResourceGroupReports(List<CostRecommendation> recommendations)
    {
        return recommendations
            .GroupBy(r => r.ResourceGroup)
            .Select(rgGroup => new ResourceGroupReport
            {
                Name = rgGroup.Key,
                Location = rgGroup.FirstOrDefault()?.Location ?? "unknown",
                Recommendations = rgGroup.Select(r => new ActionableRecommendation
                {
                    ResourceId = r.ResourceId,
                    ResourceName = r.ResourceName,
                    ResourceType = r.ResourceType,
                    Description = r.Description,
                    Action = ClassifyRecommendationAction(r),
                    Priority = r.Priority,
                    Confidence = r.Confidence.ToString("P0"), // Converter decimal para porcentagem
                    PotentialSavings = r.EstimatedMonthlySavings,
                    CurrentCost = r.EstimatedMonthlyCost,
                    DailyCost = r.DailyCost
                })
                .OrderByDescending(r => r.PotentialSavings)
                .ToList()
            })
            .OrderByDescending(rg => rg.Recommendations.Sum(r => r.PotentialSavings))
            .ToList();
    }

    /// <summary>
    /// Classifica recomendação em ação operacional
    /// </summary>
    private string ClassifyRecommendationAction(CostRecommendation recommendation)
    {
        var description = recommendation.Description.ToLowerInvariant();
        var resourceType = recommendation.ResourceType.ToLowerInvariant();

        // Log Analytics: SEMPRE "Revisar" (nunca excluir automaticamente)
        if (resourceType.Contains("operationalinsights") || resourceType.Contains("workspace"))
        {
            return "Revisar";
        }

        // App Service Plans e Function Apps: SEMPRE "Investigar" (podem ter dependências críticas)
        if (resourceType.Contains("serverfarms") || resourceType.Contains("web/sites"))
        {
            return "Investigar";
        }

        // VMs: SEMPRE "Investigar" (nunca excluir automaticamente - podem ser críticas)
        if (resourceType.Contains("virtualmachines"))
        {
            return "Investigar";
        }

        // Storage Accounts: Preferir "Investigar" (podem conter dados importantes)
        if (resourceType.Contains("storageaccounts"))
        {
            return "Investigar";
        }

        // Discos desanexados: "Investigar" (podem ser backups)
        if (resourceType.Contains("disk") && description.Contains("unattached"))
        {
            return "Investigar";
        }

        // IPs públicos não utilizados: "Investigar" (podem ter dependências externas)
        if (resourceType.Contains("publicipaddresses"))
        {
            return "Investigar";
        }

        // Ações de redução - over-provisioning
        if (description.Contains("oversize") || description.Contains("over-provision") ||
            description.Contains("reduce size") || description.Contains("downsize"))
        {
            return "Reduzir";
        }

        // Ações de revisão - duplicatas ou configurações complexas
        if (description.Contains("duplicate") || description.Contains("similar") ||
            resourceType.Contains("applicationgateway") || resourceType.Contains("loadbalancer"))
        {
            return "Revisar";
        }

        // Ações de monitoramento - baixo uso mas não crítico
        if (description.Contains("low utilization") || description.Contains("monitor") ||
            recommendation.Priority.ToLowerInvariant() == "low")
        {
            return "Monitorar";
        }

        // Default: SEMPRE "Investigar" (nunca excluir automaticamente)
        return "Investigar";
    }

    private ReportSummary BuildReportSummary(List<CostRecommendation> recommendations)
    {
        var actionBreakdown = new Dictionary<string, int>();
        var savingsByAction = new Dictionary<string, decimal>();

        foreach (var rec in recommendations)
        {
            var action = ClassifyRecommendationAction(rec);
            actionBreakdown[action] = actionBreakdown.GetValueOrDefault(action, 0) + 1;
            savingsByAction[action] = savingsByAction.GetValueOrDefault(action, 0m) + rec.EstimatedMonthlySavings;
        }

        return new ReportSummary
        {
            TotalRecommendations = recommendations.Count,
            TotalPotentialSavings = recommendations.Sum(r => r.EstimatedMonthlySavings),
            ActionBreakdown = actionBreakdown,
            SavingsByAction = savingsByAction
        };
    }

    /// <summary>
    /// Gera relatório filtrado por team - SIMPLIFICADO: filtra por subscriptions do team
    /// </summary>
    public async Task<RecommendationReport> GenerateReportByTeamAsync(DateTime analysisDate, string? teamFilter = null)
    {
        _logger.LogInformation("📊 Gerando relatório por team para {date}, team: {team}", 
            analysisDate.ToString("yyyy-MM-dd"), teamFilter ?? "todos");

        // Obter todas as recomendações
        var allRecommendations = await _storageService.GetDailyAnalysisAsync(analysisDate);
        _logger.LogInformation("📄 Carregadas {count} recommendations", allRecommendations.Count);

        // Filtrar por team se especificado
        var filteredRecommendations = allRecommendations;
        if (!string.IsNullOrEmpty(teamFilter))
        {
            // Obter subscriptions do team
            var teamSubscriptionIds = await _teamSubscriptionsService.GetTeamSubscriptionIdsAsync(teamFilter);
            
            if (teamSubscriptionIds.Count == 0)
            {
                _logger.LogWarning("⚠️ Team '{team}' não possui subscriptions configuradas", teamFilter);
            }
            else
            {
                _logger.LogInformation("🔍 Team '{team}' possui {count} subscriptions", teamFilter, teamSubscriptionIds.Count);
                
                // Filtrar recomendações que pertencem às subscriptions do team
                filteredRecommendations = allRecommendations
                    .Where(r => !string.IsNullOrEmpty(r.SubscriptionId) && 
                                teamSubscriptionIds.Any(s => s.Equals(r.SubscriptionId, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                
                _logger.LogInformation("📄 Filtrado para team '{team}': {count} recommendations", teamFilter, filteredRecommendations.Count);
            }
        }

        var report = new RecommendationReport
        {
            AnalysisDate = analysisDate,
            GeneratedAt = DateTime.UtcNow,
            ManagementGroups = await BuildManagementGroupReportsAsync(filteredRecommendations),
            Summary = BuildReportSummary(filteredRecommendations)
        };

        _logger.LogInformation("✅ Relatório gerado: {mgCount} MGs, {recCount} recomendações, R$ {savings:N2} potencial",
            report.ManagementGroups.Count, report.Summary.TotalRecommendations, report.Summary.TotalPotentialSavings);

        return report;
    }

    private static string? ExtractSubscriptionId(string resourceId)
    {
        if (string.IsNullOrEmpty(resourceId))
            return null;

        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "subscriptions", StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1];
            }
        }

        return null;
    }
}