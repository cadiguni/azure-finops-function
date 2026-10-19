using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories.Cosmos;

public abstract class CosmosRepositoryBase
{
    protected readonly Container Container;

    protected CosmosRepositoryBase(CosmosClient cosmosClient, IConfiguration configuration, string containerSettingName, string fallbackContainerName)
    {
        var databaseName = configuration["CosmosDatabaseName"] ?? "finops";
        var containerName = configuration[containerSettingName] ?? fallbackContainerName;
        Container = cosmosClient.GetContainer(databaseName, containerName);
    }
}
