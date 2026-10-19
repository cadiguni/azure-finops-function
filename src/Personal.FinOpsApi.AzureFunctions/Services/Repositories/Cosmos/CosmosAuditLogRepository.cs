using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories.Cosmos;

public class CosmosAuditLogRepository : CosmosRepositoryBase, IAuditLogRepository
{
    public CosmosAuditLogRepository(CosmosClient cosmosClient, IConfiguration configuration)
        : base(cosmosClient, configuration, "CosmosAuditLogsContainerName", "auditLogs")
    {
    }

    public Task AppendAsync(AuditLogDocument document, CancellationToken cancellationToken = default)
    {
        var partitionKey = new PartitionKey(document.EntityId);
        return Container.CreateItemAsync(document, partitionKey, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLogDocument>> GetByEntityIdAsync(string entityId, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return Array.Empty<AuditLogDocument>();
        }

        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.type = @type AND c.entityId = @entityId ORDER BY c.performedAt DESC OFFSET 0 LIMIT @limit")
            .WithParameter("@type", "auditLog")
            .WithParameter("@entityId", entityId)
            .WithParameter("@limit", Math.Max(1, limit));

        var iterator = Container.GetItemQueryIterator<AuditLogDocument>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(entityId),
                MaxItemCount = Math.Max(1, limit)
            });

        var logs = new List<AuditLogDocument>();
        while (iterator.HasMoreResults && logs.Count < limit)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            logs.AddRange(page);
        }

        return logs
            .OrderByDescending(log => log.PerformedAt)
            .Take(Math.Max(1, limit))
            .ToList();
    }
}
