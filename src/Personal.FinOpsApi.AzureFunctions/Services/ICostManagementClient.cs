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

    Task<CostByServiceQueryResponse> QueryCostByServiceForBillingMonthToDateAsync(
        string subscriptionId,
        string granularity,
        CancellationToken cancellationToken = default);

    Task<CostByResourceQueryResponse> QueryCostByResourceAsync(
        string subscriptionId,
        DateTime dateStartUtc,
        DateTime dateEndUtc,
        string granularity,
        string? serviceFilter = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<CostDimensionInfo>> GetAvailableDimensionsAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<CostDiagnosticRow>> QueryCostDiagnosticsAsync(
        string subscriptionId,
        DateTime dateStartUtc,
        DateTime dateEndUtc,
        string granularity,
        IReadOnlyCollection<string> groupingDimensions,
        CancellationToken cancellationToken = default);
}
