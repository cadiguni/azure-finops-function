using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories.Cosmos;

public class CosmosOwnershipRepository : CosmosRepositoryBase, IOwnershipRepository
{
    public CosmosOwnershipRepository(CosmosClient cosmosClient, IConfiguration configuration)
        : base(cosmosClient, configuration, "CosmosOwnershipContainerName", "ownership")
    {
    }

    public async Task<IReadOnlyList<OwnershipDocument>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.type = @type").WithParameter("@type", "ownership");
        var iterator = Container.GetItemQueryIterator<OwnershipDocument>(query);
        var items = new List<OwnershipDocument>();

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            items.AddRange(page);
        }

        return items;
    }

    public async Task<OwnershipDocument?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.id = @id").WithParameter("@id", id);
        var iterator = Container.GetItemQueryIterator<OwnershipDocument>(query, requestOptions: new QueryRequestOptions { MaxItemCount = 1 });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            var item = page.FirstOrDefault();
            if (item != null)
            {
                return item;
            }
        }

        return null;
    }

    public Task UpsertAsync(OwnershipDocument document, CancellationToken cancellationToken = default)
    {
        var partitionKey = new PartitionKey(document.TeamId);
        return Container.UpsertItemAsync(document, partitionKey, cancellationToken: cancellationToken);
    }
}
