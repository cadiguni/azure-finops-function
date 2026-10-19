using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

// Separate DI contract: the shared Blob audit repository rewrites an unbounded list without CAS.
public interface IGovernanceAuditLogRepository : IAuditLogRepository { }

/// <summary>Immutable, independently addressed events; no container creation, list rewrites or lost concurrent appends.</summary>
public sealed class GovernanceAuditLogRepository : IGovernanceAuditLogRepository
{
    private readonly BlobContainerClient _container;
    private readonly string _prefix;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public GovernanceAuditLogRepository(BlobServiceClient client, IConfiguration configuration)
    {
        var container = configuration["CONFIG_CONTAINER_NAME"];
        if (string.IsNullOrWhiteSpace(container)) throw new InvalidOperationException("CONFIG_CONTAINER_NAME obrigatório para auditoria.");
        _container = client.GetBlobContainerClient(container);
        _prefix = (configuration["GovernanceAuditBlobPrefix"] ?? "operational/governance-audit").Trim('/');
        if (string.IsNullOrWhiteSpace(_prefix)) throw new InvalidOperationException("GovernanceAuditBlobPrefix inválido.");
    }

    public async Task AppendAsync(AuditLogDocument document, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(document.EntityId, "D", out var entity) || !Guid.TryParseExact(document.Id, "D", out var id))
            throw new ArgumentException("Identificadores de auditoria inválidos.");
        // Reverse ticks allow bounded newest-first listing, including intent and result events.
        var key = $"{_prefix}/{entity:D}/{DateTime.MaxValue.Ticks - document.PerformedAt.ToUniversalTime().Ticks:D19}-{id:D}.json";
        await _container.GetBlobClient(key).UploadAsync(BinaryData.FromObjectAsJson(document, JsonOptions),
            new BlobUploadOptions { Conditions = new() { IfNoneMatch = ETag.All } }, cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLogDocument>> GetByEntityIdAsync(string entityId, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(entityId, "D", out var entity)) throw new ArgumentException("ID inválido.");
        limit = Math.Clamp(limit, 1, 100);
        var result = new List<AuditLogDocument>();
        await foreach (var page in _container.GetBlobsAsync(prefix: $"{_prefix}/{entity:D}/", cancellationToken: cancellationToken)
            .AsPages(pageSizeHint: limit))
        {
            foreach (var blob in page.Values)
            {
                var content = await _container.GetBlobClient(blob.Name).DownloadContentAsync(cancellationToken);
                result.Add(content.Value.Content.ToObjectFromJson<AuditLogDocument>(JsonOptions)
                    ?? throw new InvalidDataException("Evento de auditoria inválido."));
                if (result.Count == limit) return result;
            }
        }
        return result;
    }
}

/// <summary>A durable intent exists, but terminal audit could not be acknowledged. Never blindly retry.</summary>
public sealed class GovernanceAuditIncompleteException(string operationId, bool? applied, Exception inner)
    : Exception("Operação sem confirmação completa de escrita/auditoria. Consulte o estado e reconcilie a operação antes de tentar novamente.", inner)
{
    public string OperationId { get; } = operationId;
    public bool? Applied { get; } = applied;
}