using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories.Cosmos;

public sealed class CosmosDashboardSummaryRepository : CosmosRepositoryBase, IDashboardSummaryRepository
{
    private readonly ILogger<CosmosDashboardSummaryRepository> _logger;

    public CosmosDashboardSummaryRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        ILogger<CosmosDashboardSummaryRepository> logger)
        : base(cosmosClient, configuration, "CosmosPortalSummariesContainerName", "portalSummaries")
    {
        _logger = logger;
    }

    public async Task UpsertManyAsync(IEnumerable<DashboardSummaryDocument> documents, CancellationToken cancellationToken = default)
    {
        foreach (var document in documents.OrderBy(d => d.Type == DashboardSummaryTypes.GlobalDailySummary ? 1 : 0))
        {
            if (string.IsNullOrWhiteSpace(document.ScopeKey))
            {
                document.ScopeKey = "global";
            }

            if (document.SnapshotRevision != null && document.Type != DashboardSummaryTypes.GlobalDailySummary)
                document.Id = document.Id.Split('@')[0] + "@" + document.SnapshotRevision;
            await Container.UpsertItemAsync(
                document,
                new PartitionKey(document.ScopeKey),
                cancellationToken: cancellationToken);
        }
    }

    public async Task<DashboardSummaryDocument?> GetByTypeAndDateAsync(
        string type,
        DateTime date,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new QueryDefinition(
                    "SELECT * FROM c WHERE c.type = @type AND c.date = @date")
                .WithParameter("@type", type)
                .WithParameter("@date", date.ToString("yyyy-MM-dd"));

            var iterator = Container.GetItemQueryIterator<DashboardSummaryDocument>(query);
            DashboardSummaryDocument? latest = null;

            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(cancellationToken);
                foreach (var item in page)
                {
                    if (latest == null || item.UpdatedAt > latest.UpdatedAt)
                    {
                        latest = item;
                    }
                }
            }

            if (type != DashboardSummaryTypes.GlobalDailySummary)
            {
                var global = await GetByTypeAndDateAsync(DashboardSummaryTypes.GlobalDailySummary, date, cancellationToken);
                var items = await ListByTypeAndDateAsync(type, date, cancellationToken);
                return items.FirstOrDefault(i => i.SnapshotRevision == global?.SnapshotRevision);
            }
            return latest;
        }
        catch (CosmosException ex)
        {
            _logger.LogWarning(
                ex,
                "[COSMOS-SUMMARY] Falha em GetByTypeAndDateAsync type={type} date={date} status={statusCode} substatus={substatusCode}. Retornando null.",
                type,
                date.ToString("yyyy-MM-dd"),
                (int)ex.StatusCode,
                ex.SubStatusCode);
            return null;
        }
    }

    public async Task<IReadOnlyList<DashboardSummaryDocument>> ListByTypeAndDateAsync(
        string type,
        DateTime date,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new QueryDefinition(
                    "SELECT * FROM c WHERE c.type = @type AND c.date = @date")
                .WithParameter("@type", type)
                .WithParameter("@date", date.ToString("yyyy-MM-dd"));

            var iterator = Container.GetItemQueryIterator<DashboardSummaryDocument>(query);
            var items = new List<DashboardSummaryDocument>();
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(cancellationToken);
                items.AddRange(page);
            }

            var global = type == DashboardSummaryTypes.GlobalDailySummary ? null
                : await GetByTypeAndDateAsync(DashboardSummaryTypes.GlobalDailySummary, date, cancellationToken);
            return items
                .Where(i => type == DashboardSummaryTypes.GlobalDailySummary || i.SnapshotRevision == global?.SnapshotRevision)
                .OrderBy(i => i.TeamId)
                .ThenBy(i => i.SubscriptionId)
                .ThenByDescending(i => i.UpdatedAt)
                .ToList();
        }
        catch (CosmosException ex)
        {
            _logger.LogWarning(
                ex,
                "[COSMOS-SUMMARY] Falha em ListByTypeAndDateAsync type={type} date={date} status={statusCode} substatus={substatusCode}. Retornando lista vazia.",
                type,
                date.ToString("yyyy-MM-dd"),
                (int)ex.StatusCode,
                ex.SubStatusCode);
            return Array.Empty<DashboardSummaryDocument>();
        }
    }
}
