using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories.Cosmos;

public sealed class CosmosCostAllocationCoverageRepository : CosmosRepositoryBase, ICostAllocationCoverageRepository
{
    private readonly ILogger<CosmosCostAllocationCoverageRepository> _logger;

    public CosmosCostAllocationCoverageRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        ILogger<CosmosCostAllocationCoverageRepository> logger)
        : base(cosmosClient, configuration, "CosmosPortalSummariesContainerName", "portalSummaries")
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<CostAllocationCoverageDaily>> GetRangeAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Uma query para a janela inteira: as datas são ISO, então ordenam lexicograficamente.
            var query = new QueryDefinition(
                    "SELECT * FROM c WHERE c.type = @type AND c.date >= @from AND c.date <= @to")
                .WithParameter("@type", CostAllocationCoverageConstants.DocumentType)
                .WithParameter("@from", fromUtc.ToString("yyyy-MM-dd"))
                .WithParameter("@to", toUtc.ToString("yyyy-MM-dd"));

            var iterator = Container.GetItemQueryIterator<CostAllocationCoverageDaily>(
                query,
                requestOptions: new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey(CostAllocationCoverageConstants.ScopeKey)
                });

            var items = new List<CostAllocationCoverageDaily>();
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
                "[COST-COVERAGE] Falha ao consultar cobertura de {from} a {to}. status={statusCode}. Retornando lista vazia.",
                fromUtc.ToString("yyyy-MM-dd"),
                toUtc.ToString("yyyy-MM-dd"),
                (int)exception.StatusCode);
            return [];
        }
    }

    public async Task UpsertAsync(CostAllocationCoverageDaily coverage, CancellationToken cancellationToken = default)
    {
        coverage.ScopeKey = CostAllocationCoverageConstants.ScopeKey;
        coverage.Type = CostAllocationCoverageConstants.DocumentType;
        await Container.UpsertItemAsync(
            coverage,
            new PartitionKey(coverage.ScopeKey),
            cancellationToken: cancellationToken);
    }
}
