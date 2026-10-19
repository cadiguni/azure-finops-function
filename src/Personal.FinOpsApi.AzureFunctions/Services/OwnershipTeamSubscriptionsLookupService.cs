namespace Personal.FinOpsApi.AzureFunctions.Services;

public sealed class OwnershipTeamSubscriptionsLookupService : ITeamSubscriptionsLookupService
{
    private readonly IOwnershipConfigReader _ownershipConfigReader;
    private readonly ILegacyTeamSubscriptionsLookupService _legacyLookup;

    public OwnershipTeamSubscriptionsLookupService(
        IOwnershipConfigReader ownershipConfigReader,
        ILegacyTeamSubscriptionsLookupService legacyLookup)
    {
        _ownershipConfigReader = ownershipConfigReader;
        _legacyLookup = legacyLookup;
    }

    public async Task<Dictionary<string, string>> GetAllSubscriptionTeamMappingsAsync()
    {
        var ownership = await _ownershipConfigReader.GetConfigAsync();
        var legacyMappings = await _legacyLookup.GetAllSubscriptionTeamMappingsAsync();
        var knownSubscriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var team in ownership.Teams)
        {
            foreach (var subscription in team.Subscriptions)
            {
                if (string.IsNullOrWhiteSpace(subscription.SubscriptionId))
                {
                    continue;
                }

                knownSubscriptions.Add(subscription.SubscriptionId);
                if (subscription.Enabled && !string.IsNullOrWhiteSpace(team.Id))
                {
                    mappings[subscription.SubscriptionId] = team.Id;
                }
            }
        }

        foreach (var (subscriptionId, teamId) in legacyMappings)
        {
            if (!knownSubscriptions.Contains(subscriptionId))
            {
                mappings[subscriptionId] = teamId;
            }
        }

        return mappings;
    }
}