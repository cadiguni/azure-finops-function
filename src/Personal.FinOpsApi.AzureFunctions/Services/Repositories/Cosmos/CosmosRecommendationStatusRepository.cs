using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories.Cosmos;

public class CosmosRecommendationStatusRepository : CosmosRepositoryBase, IRecommendationStatusRepository
{
    public CosmosRecommendationStatusRepository(CosmosClient cosmosClient, IConfiguration configuration)
        : base(cosmosClient, configuration, "CosmosRecommendationStatusContainerName", "recommendationStatus")
    {
    }

    public async Task<IReadOnlyList<RecommendationStatusDocument>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.type = @type")
            .WithParameter("@type", "recommendationStatus");
        var iterator = Container.GetItemQueryIterator<RecommendationStatusDocument>(query);
        var items = new List<RecommendationStatusDocument>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            items.AddRange(page);
        }

        return items;
    }

    public async Task<IReadOnlyDictionary<string, RecommendationStatusDocument>> GetByIdsAsync(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        var distinctIds = ids
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (distinctIds.Length == 0)
        {
            return new Dictionary<string, RecommendationStatusDocument>(StringComparer.OrdinalIgnoreCase);
        }

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.type = @type AND ARRAY_CONTAINS(@ids, c.id)")
            .WithParameter("@type", "recommendationStatus")
            .WithParameter("@ids", distinctIds);

        var iterator = Container.GetItemQueryIterator<RecommendationStatusDocument>(query);
        var items = new Dictionary<string, RecommendationStatusDocument>(StringComparer.OrdinalIgnoreCase);

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            foreach (var item in page)
            {
                items[item.Id] = item;
            }
        }

        return items;
    }

    public async Task<RecommendationStatusDocument?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.id = @id").WithParameter("@id", id);
        var iterator = Container.GetItemQueryIterator<RecommendationStatusDocument>(query, requestOptions: new QueryRequestOptions { MaxItemCount = 1 });
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

    public Task UpsertAsync(RecommendationStatusDocument document, CancellationToken cancellationToken = default)
    {
        var partitionKey = new PartitionKey(document.ResourceId);
        return Container.UpsertItemAsync(document, partitionKey, cancellationToken: cancellationToken);
    }
}
