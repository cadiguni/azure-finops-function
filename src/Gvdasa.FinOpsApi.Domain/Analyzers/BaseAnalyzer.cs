using Gvdasa.FinOpsApi.Modelos.FinOps;

namespace Gvdasa.GVmodeloexemploapi.Domain.Analyzers;

public interface IResourceAnalyzer
{
    string ResourceType { get; }
    Task<IEnumerable<OptimizationFinding>> AnalyzeAsync(CostRecord costRecord, ResourceUsage? usage);
}

public abstract class BaseAnalyzer : IResourceAnalyzer
{
    protected readonly ILogger _logger;
    
    protected BaseAnalyzer(ILogger logger)
    {
        _logger = logger;
    }

    public abstract string ResourceType { get; }
    
    public abstract Task<IEnumerable<OptimizationFinding>> AnalyzeAsync(CostRecord costRecord, ResourceUsage? usage);
    
    protected OptimizationSeverity CalculateSeverity(decimal savingAmount, decimal savingPercentage)
    {
        if (savingAmount >= 1000m || savingPercentage >= 70m) return OptimizationSeverity.CRITICAL;
        if (savingAmount >= 500m || savingPercentage >= 50m) return OptimizationSeverity.HIGH;
        if (savingAmount >= 200m || savingPercentage >= 30m) return OptimizationSeverity.MEDIUM;
        return OptimizationSeverity.LOW;
    }
    
    protected OptimizationFinding CreateFinding(
        CostRecord costRecord, 
        OptimizationType type, 
        string title, 
        string description, 
        string recommendation, 
        decimal estimatedSaving,
        Dictionary<string, object>? evidence = null)
    {
        var savingPercentage = costRecord.MonthlyCost > 0 
            ? (estimatedSaving / costRecord.MonthlyCost) * 100m 
            : 0m;

        return new OptimizationFinding
        {
            ResourceId = costRecord.ResourceId,
            ResourceName = costRecord.ResourceName,
            ResourceType = costRecord.ResourceType,
            SubscriptionId = costRecord.SubscriptionId,
            ResourceGroupName = costRecord.ResourceGroupName,
            Type = type,
            Severity = CalculateSeverity(estimatedSaving, savingPercentage),
            Title = title,
            Description = description,
            Recommendation = recommendation,
            CurrentMonthlyCost = costRecord.MonthlyCost,
            EstimatedMonthlySaving = estimatedSaving,
            SavingPercentage = savingPercentage,
            DiscoveredDate = DateTime.UtcNow,
            AnalyzedDate = DateTime.UtcNow,
            IsActionable = true,
            Evidence = evidence ?? new Dictionary<string, object>()
        };
    }
}