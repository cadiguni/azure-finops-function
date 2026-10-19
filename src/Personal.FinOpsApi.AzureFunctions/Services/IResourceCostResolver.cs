using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public interface IResourceCostResolver
{
    string NormalizeResourceId(string? resourceId);

    ResourceCostResolution Resolve(
        ResourceCostResolveContext context,
        IReadOnlyCollection<CostDiagnosticRow> rows,
        DateTime periodFromUtc,
        DateTime periodToUtc);
}
