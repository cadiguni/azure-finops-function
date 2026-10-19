using Azure.Storage.Blobs;
using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Salva e carrega relatórios de anomalias de custo no Blob Storage.
/// 
/// Path: cost-anomalies/{yyyy}/{MM}/{dd}/subscription-anomalies.json
/// Container: finops-analysis (CostAnomalyStorageContainer)
/// </summary>
public class CostAnomalyStorageService
{
    private const string LatestBlobPath = "cost-anomalies/latest.json";
    private readonly BlobContainerClient _container;
    private readonly ILogger<CostAnomalyStorageService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public CostAnomalyStorageService(
        BlobServiceClient blobServiceClient,
        IConfiguration configuration,
        ILogger<CostAnomalyStorageService> logger)
    {
        var containerName = configuration["CostAnomalyStorageContainer"] ?? "finops-analysis";
        _container = blobServiceClient.GetBlobContainerClient(containerName);
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    /// <summary>
    /// Salva relatório de anomalias no Blob Storage
    /// </summary>
    public async Task SaveAnomalyReportAsync(CostAnomalyReport report, CancellationToken cancellationToken = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var reportDate = !string.IsNullOrWhiteSpace(report.RequestedDate) ? report.RequestedDate : report.Date;
        var blobPath = BuildBlobPath(reportDate);
        var blobClient = _container.GetBlobClient(blobPath);

        var json = JsonSerializer.Serialize(report, _jsonOptions);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await blobClient.UploadAsync(stream, overwrite: true, cancellationToken: cancellationToken);
        await _container.GetBlobClient(LatestBlobPath)
            .UploadAsync(BinaryData.FromString(json), overwrite: true, cancellationToken: cancellationToken);

        _logger.LogInformation(
            "[COST-ANOMALY-STORAGE] Relatório salvo: {path} ({anomalies} anomalias, {total} subscriptions)",
            blobPath, report.TotalAnomaliesDetected, report.TotalSubscriptionsAnalyzed);
    }

    /// <summary>
    /// Carrega relatório de anomalias de uma data específica
    /// </summary>
    public async Task<CostAnomalyReport?> LoadAnomalyReportAsync(string date, CancellationToken cancellationToken = default)
    {
        var blobPath = BuildBlobPath(date);
        return await LoadAnomalyReportByPathAsync(blobPath, cancellationToken);
    }

    public async Task<CostAnomalyReport?> LoadLatestAnomalyReportAsync(CancellationToken cancellationToken = default)
    {
        return await LoadAnomalyReportByPathAsync(LatestBlobPath, cancellationToken);
    }

    public async Task SaveRunManifestAsync(
        CostAnomalyRunManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ValidateRunId(manifest.RunId);
        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var json = JsonSerializer.Serialize(manifest, _jsonOptions);
        await _container.GetBlobClient(BuildRunManifestPath(manifest.RunId))
            .UploadAsync(BinaryData.FromString(json), overwrite: true, cancellationToken: cancellationToken);
    }

    public async Task<CostAnomalyRunManifest> LoadRunManifestAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ValidateRunId(runId);
        var blobClient = _container.GetBlobClient(BuildRunManifestPath(runId));
        if (!await blobClient.ExistsAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Manifesto da execução {runId} não encontrado.");
        }

        var content = await blobClient.DownloadContentAsync(cancellationToken);
        return content.Value.Content.ToObjectFromJson<CostAnomalyRunManifest>(_jsonOptions)
            ?? throw new InvalidOperationException($"Manifesto da execução {runId} é inválido.");
    }

    public async Task SaveRunSubscriptionResultAsync(
        string runId,
        int subscriptionIndex,
        SubscriptionCostAnomaly result,
        CancellationToken cancellationToken = default)
    {
        ValidateRunId(runId);
        var json = JsonSerializer.Serialize(result, _jsonOptions);
        await _container.GetBlobClient(BuildRunResultPath(runId, subscriptionIndex))
            .UploadAsync(BinaryData.FromString(json), overwrite: true, cancellationToken: cancellationToken);
    }

    public async Task<List<SubscriptionCostAnomaly>> LoadRunSubscriptionResultsAsync(
        string runId,
        int subscriptionCount,
        CancellationToken cancellationToken = default)
    {
        ValidateRunId(runId);
        var results = new List<SubscriptionCostAnomaly>(subscriptionCount);
        for (var index = 0; index < subscriptionCount; index++)
        {
            var blobClient = _container.GetBlobClient(BuildRunResultPath(runId, index));
            if (!await blobClient.ExistsAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Resultado {index + 1}/{subscriptionCount} da execução {runId} não encontrado.");
            }

            var content = await blobClient.DownloadContentAsync(cancellationToken);
            var result = content.Value.Content.ToObjectFromJson<SubscriptionCostAnomaly>(_jsonOptions)
                ?? throw new InvalidOperationException(
                    $"Resultado {index + 1}/{subscriptionCount} da execução {runId} é inválido.");
            results.Add(result);
        }

        return results;
    }

    private static string BuildRunManifestPath(string runId)
        => $"cost-anomalies/runs/{runId}/manifest.json";

    private static string BuildRunResultPath(string runId, int subscriptionIndex)
        => $"cost-anomalies/runs/{runId}/subscriptions/{subscriptionIndex:D4}.json";

    private static void ValidateRunId(string runId)
    {
        if (!Guid.TryParse(runId, out _))
        {
            throw new ArgumentException("RunId inválido.", nameof(runId));
        }
    }

    private static string BuildBlobPath(string date)
    {
        // Parse date para garantir formato correto
        if (DateTime.TryParse(date, out var parsedDate))
        {
            return $"cost-anomalies/{parsedDate:yyyy}/{parsedDate:MM}/{parsedDate:dd}/subscription-anomalies.json";
        }

        return $"cost-anomalies/{date}/subscription-anomalies.json";
    }

    private async Task<CostAnomalyReport?> LoadAnomalyReportByPathAsync(string blobPath, CancellationToken cancellationToken)
    {
        var blobClient = _container.GetBlobClient(blobPath);

        if (!await blobClient.ExistsAsync(cancellationToken))
        {
            _logger.LogInformation("[COST-ANOMALY-STORAGE] Relatório não encontrado: {path}", blobPath);
            return null;
        }

        var downloadResult = await blobClient.DownloadContentAsync(cancellationToken);
        var json = downloadResult.Value.Content.ToString();
        var report = JsonSerializer.Deserialize<CostAnomalyReport>(json, _jsonOptions);
        if (report == null)
        {
            return null;
        }

        NormalizeReport(report);
        return report;
    }

    private static void NormalizeReport(CostAnomalyReport report)
    {
        if (string.IsNullOrWhiteSpace(report.RequestedDate))
        {
            report.RequestedDate = report.Date;
        }

        if (string.IsNullOrWhiteSpace(report.Date))
        {
            report.Date = report.RequestedDate;
        }

        foreach (var subscription in report.Subscriptions)
        {
            if (string.IsNullOrWhiteSpace(subscription.RequestedDate))
            {
                subscription.RequestedDate = report.RequestedDate;
            }

            if (string.IsNullOrWhiteSpace(subscription.DataStatus))
            {
                subscription.DataStatus = CostAnomalyDataStatus.Consolidated;
            }
        }
    }
}
