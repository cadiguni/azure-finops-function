using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public sealed class BlobReportSnapshotStore : IReportSnapshotStore
{
    private readonly BlobContainerClient _container;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public BlobReportSnapshotStore(BlobServiceClient blobs, IConfiguration configuration)
        => _container = blobs.GetBlobContainerClient(configuration["RESULTS_CONTAINER_NAME"] ?? "finops-analysis");

    private BlobClient Blob(string revision)
    {
        if (!Guid.TryParseExact(revision, "N", out _)) throw new InvalidDataException("Revisão de relatório inválida.");
        return _container.GetBlobClient($"reports/snapshots/{revision}.json");
    }

    public async Task SaveAsync(string revision, IReadOnlyDictionary<string, RecommendationReport> reports, CancellationToken cancellationToken = default)
    {
        await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        await Blob(revision).UploadAsync(BinaryData.FromObjectAsJson(reports, Json),
            new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, cancellationToken);
    }

    public async Task<RecommendationReport?> LoadAsync(string revision, string scope, CancellationToken cancellationToken = default)
    {
        try
        {
            var content = await Blob(revision).DownloadContentAsync(cancellationToken);
            var reports = content.Value.Content.ToObjectFromJson<Dictionary<string, RecommendationReport>>(Json)
                ?? throw new InvalidDataException("Snapshot inválido.");
            var report = reports.FirstOrDefault(p => p.Key.Equals(scope, StringComparison.OrdinalIgnoreCase)).Value;
            if (report != null && report.SnapshotRevision != revision) throw new InvalidDataException("Revisão do relatório não confere.");
            return report;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }
}
