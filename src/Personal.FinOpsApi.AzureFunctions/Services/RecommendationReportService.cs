using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Modelo unificado de relatório de recomendações
/// </summary>
public class RecommendationReport
{
    public string? SnapshotRevision { get; set; }
    public CostReportFinancialSummary? FinancialSummary { get; set; }
    public string PeriodFrom { get; set; } = "";
    public string PeriodTo { get; set; } = "";
    public string Scope { get; set; } = "global";
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
    /// <summary>Currency of TotalPotentialSavings and of every savings subtotal.</summary>
    public string? SavingsCurrency { get; set; }
    public decimal? ObservedCostTotal { get; set; }
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
    public decimal? ObservedCost { get; set; }
    public CostReadProvenance? CostProvenance { get; set; }
    public string CostScope { get; set; } = "Missing";
    public string CostGroupKey { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime? CostPeriodFrom { get; set; }
    public DateTime? CostPeriodTo { get; set; }
    public string RecommendationText { get; set; } = string.Empty;
    public string RecommendationType { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
    public string CostSource { get; set; } = CostMatchSources.NotMatched;
    public string CostMatchConfidence { get; set; } = CostMatchConfidenceLevels.Low;
    public string? CostMatchedResourceId { get; set; }
    public string CostNote { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // Excluir, Reduzir, Revisar, Monitorar
    public string Priority { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;
    public decimal PotentialSavings { get; set; }
    public string SavingsStatus { get; set; } = string.Empty;
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
    private readonly Repositories.IDashboardSummaryRepository _summaries;
    private readonly IReportSnapshotStore _snapshots;

    public RecommendationReportService(Repositories.IDashboardSummaryRepository summaries, IReportSnapshotStore snapshots)
    {
        _summaries = summaries;
        _snapshots = snapshots;
    }

    public Task<RecommendationReport> GenerateReportAsync(DateTime analysisDate, string? managementGroupFilter = null, string? subscriptionFilter = null)
    {
        if (!string.IsNullOrWhiteSpace(managementGroupFilter))
            throw new ArgumentException("Use filtro por time ou assinatura; managementGroup não possui snapshot financeiro.");
        return LoadAsync(analysisDate, string.IsNullOrWhiteSpace(subscriptionFilter) ? "global" : $"subscription:{subscriptionFilter}");
    }

    public Task<RecommendationReport> GenerateReportByTeamAsync(DateTime analysisDate, string? teamFilter = null)
        => LoadAsync(analysisDate, string.IsNullOrWhiteSpace(teamFilter) ? "global" : $"team:{teamFilter}");

    private async Task<RecommendationReport> LoadAsync(DateTime date, string scope)
    {
        var (_, global) = await DashboardSnapshotSelection.ResolveAsync(_summaries, date);
        if (global?.SnapshotRevision == null)
            throw new FileNotFoundException("Snapshot financeiro versionado ainda não disponível. Aguarde a geração dos sumários.");
        return await _snapshots.LoadAsync(global.SnapshotRevision, scope)
            ?? throw new FileNotFoundException("Escopo não encontrado no snapshot financeiro selecionado.");
    }

    public static RecommendationReport BuildSnapshot(DashboardSummaryDocument summary,
        IReadOnlyList<(CostRecommendation Recommendation, string Status, bool Active)> items)
    {
        // Assign each charged resource's potential to one action, deterministically. Each amount stays in
        // its own currency: a mixed-currency scope only prevents a scalar total, not per-resource savings.
        var allocated = new Dictionary<CostRecommendation, decimal>();
        var reportCurrency = summary.FinancialSummary?.Currency;
        foreach (var group in items.Where(item => item.Active)
                     .GroupBy(item => (RecommendationCostAccounting.Key(item.Recommendation),
                         item.Recommendation.CostCurrency.Trim().ToUpperInvariant())))
        {
            var winner = group.OrderByDescending(i => RecommendationCostAccounting.Savings(new[] { i.Recommendation }))
                .ThenBy(i => i.Recommendation.Type, StringComparer.Ordinal).First().Recommendation;
            allocated[winner] = RecommendationCostAccounting.Savings(group.Select(i => i.Recommendation));
        }
        var savingsCurrency = summary.SavingsCurrency ?? RecommendationCostAccounting.SavingsCurrency(
            items.Where(item => item.Active).Select(item => item.Recommendation), reportCurrency);
        decimal Totalled(ActionableRecommendation r) => savingsCurrency != null
            && r.Currency.Trim().Equals(savingsCurrency, StringComparison.OrdinalIgnoreCase) ? r.PotentialSavings : 0m;
        var report = new RecommendationReport
        {
            AnalysisDate = DateTime.ParseExact(summary.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            GeneratedAt = summary.UpdatedAt, SnapshotRevision = summary.SnapshotRevision,
            FinancialSummary = summary.FinancialSummary, Scope = summary.ScopeKey,
            PeriodFrom = summary.PeriodFrom, PeriodTo = summary.PeriodTo,
            Currency = summary.FinancialSummary?.Currency ?? savingsCurrency ?? "",
            Summary = new ReportSummary { TotalRecommendations = items.Count,
                SavingsCurrency = savingsCurrency,
                ObservedCostTotal = RecommendationCostAccounting.ObservedTotal(
                    items.Select(item => item.Recommendation), reportCurrency ?? savingsCurrency) }
        };
        foreach (var mg in items.GroupBy(i => i.Recommendation.ManagementGroupId ?? "unassigned"))
        {
            var mgReport = new ManagementGroupReport { Id = mg.Key, Name = mg.Key, TotalRecommendations = mg.Count() };
            foreach (var sub in mg.GroupBy(i => i.Recommendation.SubscriptionId))
            {
                var subReport = new SubscriptionReport { Id = sub.Key, Name = sub.Key, TotalRecommendations = sub.Count() };
                foreach (var rg in sub.GroupBy(i => i.Recommendation.ResourceGroup))
                {
                    var rgReport = new ResourceGroupReport
                    {
                        Name = rg.Key,
                        Location = rg.First().Recommendation.Location
                    };
                    foreach (var item in rg)
                    {
                        var r = item.Recommendation;
                        var action = ClassifyRecommendationAction(r);
                        var savings = allocated.GetValueOrDefault(r);
                        var actionable = new ActionableRecommendation
                        {
                            ResourceId = r.ResourceId, ResourceName = r.ResourceName, ResourceType = r.ResourceType,
                            Description = r.Description, Action = action, Priority = r.Priority,
                            RecommendationText = r.Recommendation, RecommendationType = r.Type, Impact = r.Impact,
                            Confidence = r.Confidence.ToString("P0"), Status = item.Status,
                            CurrentCost = r.ObservedCost.HasValue ? r.EstimatedMonthlyCost : 0,
                            DailyCost = r.ObservedCost.HasValue ? r.DailyCost : 0,
                            PotentialSavings = savings,
                            SavingsStatus = savings > 0 ? "Quantified"
                                : r.EstimatedMonthlySavings > 0 ? "ExcludedFromTotal" : "NotEstimated",
                            ObservedCost = r.ObservedCost,
                            CostProvenance = r.CostProvenance, CostScope = RecommendationCostAccounting.Scope(r),
                            CostGroupKey = RecommendationCostAccounting.Key(r), Currency = r.CostCurrency,
                            CostPeriodFrom = r.CostPeriodFrom, CostPeriodTo = r.CostPeriodTo,
                            CostSource = r.CostSource, CostMatchConfidence = r.CostMatchConfidence,
                            CostMatchedResourceId = r.CostMatchedResourceId, CostNote = r.CostNote
                        };
                        rgReport.Recommendations.Add(actionable);
                        report.Summary.ActionBreakdown[action] = report.Summary.ActionBreakdown.GetValueOrDefault(action) + 1;
                        report.Summary.SavingsByAction[action] = report.Summary.SavingsByAction.GetValueOrDefault(action) + Totalled(actionable);
                    }
                    subReport.ResourceGroups.Add(rgReport);
                }
                subReport.TotalSavings = subReport.ResourceGroups.Sum(g => g.Recommendations.Sum(Totalled));
                mgReport.Subscriptions.Add(subReport);
            }
            mgReport.TotalSavings = mgReport.Subscriptions.Sum(s => s.TotalSavings);
            report.ManagementGroups.Add(mgReport);
        }
        report.Summary.TotalPotentialSavings = report.ManagementGroups.Sum(mg => mg.TotalSavings);
        return report;
    }

    private static string ClassifyRecommendationAction(CostRecommendation recommendation)
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

}
