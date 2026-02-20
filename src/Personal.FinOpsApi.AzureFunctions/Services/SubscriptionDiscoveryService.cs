using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Azure.ResourceManager;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public class SubscriptionDiscoveryService
{
    private readonly ArmClient _armClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SubscriptionDiscoveryService> _logger;

    public SubscriptionDiscoveryService(
        ArmClient armClient,
        IConfiguration configuration,
        ILogger<SubscriptionDiscoveryService> logger)
    {
        _armClient = armClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<string>> DiscoverSubscriptionsAsync()
    {
        try
        {
            var manualSubscriptions = GetManualSubscriptions();
            if (manualSubscriptions.Any())
            {
                _logger.LogInformation("Using {count} subscriptions from AZURE_SUBSCRIPTION_IDS", manualSubscriptions.Count);
                return manualSubscriptions;
            }

            var managementGroupId = _configuration["AZURE_MANAGEMENT_GROUP_ID"];
            if (!string.IsNullOrWhiteSpace(managementGroupId))
            {
                var mgSubscriptions = await GetSubscriptionsFromManagementGroupAsync(managementGroupId);
                if (mgSubscriptions.Any())
                {
                    return mgSubscriptions;
                }
            }

            var discoverAllTenant = _configuration.GetValue<bool>("AZURE_DISCOVER_ALL_SUBSCRIPTIONS", false);
            if (discoverAllTenant)
            {
                var tenantSubscriptions = await GetAllAccessibleSubscriptionsAsync();
                if (tenantSubscriptions.Any())
                {
                    return tenantSubscriptions;
                }
            }

            return GetFallbackSubscription();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error discovering subscriptions. Using fallback configuration.");
            return GetFallbackSubscription();
        }
    }

    private List<string> GetManualSubscriptions()
    {
        var subscriptionsEnv = _configuration["AZURE_SUBSCRIPTION_IDS"];
        if (string.IsNullOrWhiteSpace(subscriptionsEnv))
        {
            return new List<string>();
        }

        return subscriptionsEnv
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<string>> GetSubscriptionsFromManagementGroupAsync(string managementGroupId)
    {
        try
        {
            var subscriptions = new List<string>();

            await foreach (var subscription in _armClient.GetSubscriptions().GetAllAsync())
            {
                if (subscription.Data.State == Azure.ResourceManager.Resources.Models.SubscriptionState.Enabled)
                {
                    subscriptions.Add(subscription.Data.SubscriptionId);
                }
            }

            _logger.LogInformation(
                "Found {count} active subscriptions while scanning management group {managementGroupId}",
                subscriptions.Count,
                managementGroupId);

            return subscriptions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching subscriptions from management group {managementGroupId}", managementGroupId);
            return new List<string>();
        }
    }

    private async Task<List<string>> GetAllAccessibleSubscriptionsAsync()
    {
        try
        {
            var subscriptions = new List<string>();

            await foreach (var subscription in _armClient.GetSubscriptions().GetAllAsync())
            {
                if (subscription.Data.State == Azure.ResourceManager.Resources.Models.SubscriptionState.Enabled)
                {
                    subscriptions.Add(subscription.Data.SubscriptionId);
                }
            }

            return subscriptions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching tenant subscriptions");
            return new List<string>();
        }
    }

    private List<string> GetFallbackSubscription()
    {
        var subscriptionId = _configuration["AZURE_SUBSCRIPTION_ID"];
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            _logger.LogWarning("No subscriptions configured. Set AZURE_SUBSCRIPTION_IDS or AZURE_SUBSCRIPTION_ID.");
            return new List<string>();
        }

        return new List<string> { subscriptionId.Trim() };
    }

    public async Task<Dictionary<string, object>> GetSubscriptionDetailsAsync(List<string> subscriptionIds)
    {
        var details = new Dictionary<string, object>();

        foreach (var subscriptionId in subscriptionIds)
        {
            try
            {
                var subscription = await _armClient.GetSubscriptions().GetAsync(subscriptionId);
                details[subscriptionId] = new
                {
                    subscription_id = subscriptionId,
                    display_name = subscription.Value.Data.DisplayName,
                    state = subscription.Value.Data.State.ToString(),
                    tenant_id = subscription.Value.Data.TenantId?.ToString(),
                    subscription_policies = subscription.Value.Data.SubscriptionPolicies?.SpendingLimit?.ToString()
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read details for subscription {subscriptionId}", subscriptionId);
                details[subscriptionId] = new
                {
                    subscription_id = subscriptionId,
                    error = ex.Message
                };
            }
        }

        return details;
    }
}
