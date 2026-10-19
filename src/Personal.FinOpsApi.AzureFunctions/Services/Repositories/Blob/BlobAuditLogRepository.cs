using Azure.Storage.Blobs;
using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories.Blob;

public class BlobAuditLogRepository : IAuditLogRepository
{
    private readonly BlobRepositoryStore _store;
    private readonly string _blobPath;

    public BlobAuditLogRepository(BlobServiceClient blobServiceClient, IConfiguration configuration)
    {
        var containerName = configuration["CONFIG_CONTAINER_NAME"] ?? "finops-config";
        _blobPath = configuration["AuditLogsBlobPath"] ?? "operational/audit-logs.json";
        _store = new BlobRepositoryStore(blobServiceClient, containerName);
    }

    public async Task AppendAsync(AuditLogDocument document, CancellationToken cancellationToken = default)
    {
        var logs = await _store.LoadListAsync<AuditLogDocument>(_blobPath, cancellationToken);
        logs.Add(document);
        await _store.SaveListAsync(_blobPath, logs, cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLogDocument>> GetByEntityIdAsync(string entityId, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return Array.Empty<AuditLogDocument>();
        }

        var logs = await _store.LoadListAsync<AuditLogDocument>(_blobPath, cancellationToken);
        return logs
            .Where(log => log.EntityId.Equals(entityId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(log => log.PerformedAt)
            .Take(Math.Max(1, limit))
            .ToList();
    }
}
