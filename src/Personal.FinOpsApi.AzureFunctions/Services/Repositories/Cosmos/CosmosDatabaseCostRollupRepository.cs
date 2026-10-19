using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories.Cosmos;

public sealed class CosmosDatabaseCostRollupRepository : CosmosRepositoryBase, IDatabaseCostRollupRepository
{
    private readonly ILogger<CosmosDatabaseCostRollupRepository> _logger;

    public CosmosDatabaseCostRollupRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        ILogger<CosmosDatabaseCostRollupRepository> logger)
        : base(cosmosClient, configuration, "CosmosPortalSummariesContainerName", "portalSummaries")
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<CostDatabaseDailyRollup>> GetRangeAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // One query for the whole window: the dates are ISO strings, so they order lexicographically.
            var query = new QueryDefinition(
                    "SELECT * FROM c WHERE c.type = @type AND c.date >= @from AND c.date <= @to")
                .WithParameter("@type", CostDatabaseRollupConstants.DocumentType)
                .WithParameter("@from", fromUtc.ToString("yyyy-MM-dd"))
                .WithParameter("@to", toUtc.ToString("yyyy-MM-dd"));

            var iterator = Container.GetItemQueryIterator<CostDatabaseDailyRollup>(
                query,
                requestOptions: new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey(CostDatabaseRollupConstants.ScopeKey)
                });

            var items = new List<CostDatabaseDailyRollup>();
            while (iterator.HasMoreResults)
            {
                items.AddRange(await iterator.ReadNextAsync(cancellationToken));
            }

            return items
                .GroupBy(item => item.Date, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.GeneratedAt).First())
                .OrderBy(item => item.Date, StringComparer.Ordinal)
                .ToList();
        }
        catch (CosmosException exception)
        {
            _logger.LogWarning(
                exception,
                "[DB-COST-ROLLUP] Falha ao consultar rollups de {from} a {to}. status={statusCode}. Retornando lista vazia.",
                fromUtc.ToString("yyyy-MM-dd"),
                toUtc.ToString("yyyy-MM-dd"),
                (int)exception.StatusCode);
            return [];
        }
    }

    public async Task UpsertAsync(CostDatabaseDailyRollup rollup, CancellationToken cancellationToken = default)
    {
        rollup.ScopeKey = CostDatabaseRollupConstants.ScopeKey;
        rollup.Type = CostDatabaseRollupConstants.DocumentType;
        await Container.UpsertItemAsync(
            rollup,
            new PartitionKey(rollup.ScopeKey),
            cancellationToken: cancellationToken);
    }
}
