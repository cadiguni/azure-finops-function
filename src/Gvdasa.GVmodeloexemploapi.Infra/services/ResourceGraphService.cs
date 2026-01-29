using Gvdasa.GVmodeloexemploapi.Modelos.FinOps;

namespace Gvdasa.GVmodeloexemploapi.Infra.Services.FinOps;

public interface IResourceGraphService
{
    Task<IEnumerable<string>> GetUnattachedDisksAsync(string subscriptionId);
    Task<IEnumerable<string>> GetUnassignedPublicIpsAsync(string subscriptionId);
    Task<IEnumerable<string>> GetInactiveVmsAsync(string subscriptionId, int daysInactive = 30);
    Task<IEnumerable<string>> GetLowTrafficAppServicesAsync(string subscriptionId);
    Task<IEnumerable<string>> GetResourcesByTypeAsync(string subscriptionId, string resourceType);
    Task<IEnumerable<ResourceGraphResult>> ExecuteCustomQueryAsync(string query, string[]? subscriptions = null);
}

public class ResourceGraphService : IResourceGraphService
{
    private readonly ILogger<ResourceGraphService> _logger;
    private readonly HttpClient _httpClient;

    public ResourceGraphService(ILogger<ResourceGraphService> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<IEnumerable<string>> GetUnattachedDisksAsync(string subscriptionId)
    {
        const string query = @"
            Resources
            | where type == 'microsoft.compute/disks'
            | where properties.diskState == 'Unattached'
            | project id, name, resourceGroup, subscriptionId";
            
        var results = await ExecuteQueryAsync(query, new[] { subscriptionId });
        return results.Select(r => r.Id);
    }

    public async Task<IEnumerable<string>> GetUnassignedPublicIpsAsync(string subscriptionId)
    {
        const string query = @"
            Resources
            | where type == 'microsoft.network/publicipaddresses'
            | where isnull(properties.ipConfiguration)
            | project id, name, resourceGroup, subscriptionId";
            
        var results = await ExecuteQueryAsync(query, new[] { subscriptionId });
        return results.Select(r => r.Id);
    }

    public async Task<IEnumerable<string>> GetInactiveVmsAsync(string subscriptionId, int daysInactive = 30)
    {
        // Query para VMs que estão desligadas há X dias
        var query = $@"
            Resources
            | where type == 'microsoft.compute/virtualmachines'
            | where properties.extended.instanceView.powerState.displayStatus == 'VM deallocated'
            | extend lastActivity = properties.extended.instanceView.statuses[0].time
            | where datetime_diff('day', now(), todatetime(lastActivity)) > {daysInactive}
            | project id, name, resourceGroup, subscriptionId, lastActivity";
            
        var results = await ExecuteQueryAsync(query, new[] { subscriptionId });
        return results.Select(r => r.Id);
    }

    public async Task<IEnumerable<string>> GetLowTrafficAppServicesAsync(string subscriptionId)
    {
        // Query básica para App Services - refinamento com métricas será feito no analyzer
        const string query = @"
            Resources
            | where type == 'microsoft.web/sites'
            | where kind != 'functionapp'
            | project id, name, resourceGroup, subscriptionId, sku = properties.sku";
            
        var results = await ExecuteQueryAsync(query, new[] { subscriptionId });
        return results.Select(r => r.Id);
    }

    public async Task<IEnumerable<string>> GetResourcesByTypeAsync(string subscriptionId, string resourceType)
    {
        var query = $@"
            Resources
            | where type == '{resourceType.ToLower()}'
            | project id, name, resourceGroup, subscriptionId";
            
        var results = await ExecuteQueryAsync(query, new[] { subscriptionId });
        return results.Select(r => r.Id);
    }

    public async Task<IEnumerable<ResourceGraphResult>> ExecuteCustomQueryAsync(string query, string[]? subscriptions = null)
    {
        return await ExecuteQueryAsync(query, subscriptions);
    }

    private async Task<IEnumerable<ResourceGraphResult>> ExecuteQueryAsync(string query, string[]? subscriptions = null)
    {
        try
        {
            _logger.LogInformation("Executando query Resource Graph: {Query}", query.Replace("\n", " ").Replace("\r", ""));
            
            // TODO: Implementar chamada real para Azure Resource Graph API
            // https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01
            
            // Mock para demonstração
            await Task.Delay(100);
            
            return new List<ResourceGraphResult>
            {
                new ResourceGraphResult
                {
                    Id = "/subscriptions/example/resourceGroups/rg1/providers/Microsoft.Compute/disks/unattached-disk1",
                    Name = "unattached-disk1",
                    ResourceGroup = "rg1",
                    SubscriptionId = subscriptions?.FirstOrDefault() ?? "example-subscription",
                    AdditionalProperties = new Dictionary<string, object>
                    {
                        ["diskState"] = "Unattached",
                        ["sizeGB"] = 128
                    }
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao executar query Resource Graph");
            throw;
        }
    }
}

public class ResourceGraphResult
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public Dictionary<string, object> AdditionalProperties { get; set; } = new();
}