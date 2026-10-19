using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public interface IGovernanceBudgetRepository
{
    Task<IReadOnlyList<GovernanceBudget>> ListAsync(CancellationToken cancellationToken = default);
    Task<GovernanceBudget?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task<GovernanceBudget> SaveAsync(GovernanceBudget budget, string? expectedVersion, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, string expectedVersion, CancellationToken cancellationToken = default);
}

/// <summary>One blob per budget. Conditional writes prevent lost updates; reads never create containers.</summary>
public sealed class BlobGovernanceBudgetRepository : IGovernanceBudgetRepository
{
    private readonly BlobContainerClient _container;
    private readonly string _prefix;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public BlobGovernanceBudgetRepository(BlobServiceClient client, IConfiguration configuration)
    {
        var container = configuration["CONFIG_CONTAINER_NAME"];
        if (string.IsNullOrWhiteSpace(container))
            throw new InvalidOperationException("CONFIG_CONTAINER_NAME é obrigatório para orçamentos.");
        _container = client.GetBlobContainerClient(container);
        _prefix = (configuration["BudgetsBlobPrefix"] ?? "operational/budgets").Trim('/');
        if (string.IsNullOrWhiteSpace(_prefix)) throw new InvalidOperationException("BudgetsBlobPrefix inválido.");
    }

    public async Task<IReadOnlyList<GovernanceBudget>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<GovernanceBudget>();
        var scanned = 0;
        await foreach (var page in _container.GetBlobsAsync(prefix: _prefix + "/", cancellationToken: cancellationToken)
            .AsPages(pageSizeHint: 101))
        {
            foreach (var blob in page.Values)
            {
                // Bound scanned entries, including invalid entries and concurrently deleted documents.
                if (++scanned > 100) throw new InvalidOperationException("Limite de 100 entradas por listagem de orçamentos excedido.");
                if (!blob.Name.EndsWith(".json", StringComparison.Ordinal)) continue;
                var id = Path.GetFileNameWithoutExtension(blob.Name);
                var budget = await GetAsync(id, cancellationToken);
                if (budget is not null) result.Add(budget);
            }
        }
        return result.OrderByDescending(b => b.UpdatedAt).ToArray();
    }

    public async Task<GovernanceBudget?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Blob(id).DownloadContentAsync(cancellationToken);
            var budget = result.Value.Content.ToObjectFromJson<GovernanceBudget>(JsonOptions)
                ?? throw new InvalidDataException("Documento de orçamento inválido.");
            return budget with { Version = result.Value.Details.ETag.ToString() };
        }
        catch (RequestFailedException ex) when (ex.Status == 404 && ex.ErrorCode == "BlobNotFound") { return null; }
    }

    public async Task<GovernanceBudget> SaveAsync(GovernanceBudget budget, string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var response = await Blob(budget.Id).UploadAsync(BinaryData.FromObjectAsJson(budget, JsonOptions),
            new BlobUploadOptions { Conditions = WriteConditions(expectedVersion) }, cancellationToken);
        return budget with { Version = response.Value.ETag.ToString() };
    }

    public async Task DeleteAsync(string id, string expectedVersion, CancellationToken cancellationToken = default) =>
        await Blob(id).DeleteAsync(DeleteSnapshotsOption.IncludeSnapshots,
            WriteConditions(expectedVersion), cancellationToken);

    internal static BlobRequestConditions WriteConditions(string? expectedVersion)
    {
        if (expectedVersion is null) return new() { IfNoneMatch = ETag.All };
        if (string.IsNullOrWhiteSpace(expectedVersion) || expectedVersion.Length is < 3 or > 128 ||
            expectedVersion[1..^1].Any(c => c < 0x21 || c > 0x7e || c == '"') ||
            !expectedVersion.StartsWith('"') || !expectedVersion.EndsWith('"'))
            throw new ArgumentException("Versão inválida; recarregue o orçamento.");
        return new() { IfMatch = new ETag(expectedVersion) };
    }

    private BlobClient Blob(string id)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed)) throw new ArgumentException("ID de orçamento inválido.");
        return _container.GetBlobClient($"{_prefix}/{parsed:D}.json");
    }
}