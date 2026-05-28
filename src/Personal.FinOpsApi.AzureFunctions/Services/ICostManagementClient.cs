using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public interface ICostManagementClient
{
    Task<CostByServiceQueryResponse> QueryCostByServiceAsync(
        string subscriptionId,
        DateTime dateStartUtc,
        DateTime dateEndUtc,
        string granularity,
        string? serviceFilter = null,
        CancellationToken cancellationToken = default);

    Task<CostByResourceQueryResponse> QueryCostByResourceAsync(
        string subscriptionId,
        DateTime dateStartUtc,
        DateTime dateEndUtc,
        string granularity,
        string? serviceFilter = null,
        CancellationToken cancellationToken = default);
}
