using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public interface IBillingPeriodResolver
{
    Task<BillingPeriodInfo> ResolveAsync(
        string subscriptionId,
        DateTime date,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BillingPeriodOverrideInfo>> ListOverridesAsync(
        CancellationToken cancellationToken = default);

    Task<BillingPeriodOverrideInfo> UpsertOverrideAsync(
        string subscriptionId,
        int billingCycleStartDay,
        string updatedBy,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteOverrideAsync(
        string subscriptionId,
        string updatedBy,
        CancellationToken cancellationToken = default);
}
