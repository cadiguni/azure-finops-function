namespace Personal.FinOpsApi.AzureFunctions.Services;

public interface ITeamSubscriptionsLookupService
{
    Task<Dictionary<string, string>> GetAllSubscriptionTeamMappingsAsync();
}

public interface ILegacyTeamSubscriptionsLookupService
{
    Task<Dictionary<string, string>> GetAllSubscriptionTeamMappingsAsync();
}

public interface IOwnershipConfigReader
{
    Task<Models.OwnershipConfig> GetConfigAsync(CancellationToken cancellationToken = default);
}
