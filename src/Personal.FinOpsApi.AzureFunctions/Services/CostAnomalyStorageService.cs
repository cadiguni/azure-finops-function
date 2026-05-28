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

        var blobPath = BuildBlobPath(report.Date);
        var blobClient = _container.GetBlobClient(blobPath);

        var json = JsonSerializer.Serialize(report, _jsonOptions);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await blobClient.UploadAsync(stream, overwrite: true, cancellationToken: cancellationToken);

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
        var blobClient = _container.GetBlobClient(blobPath);

        if (!await blobClient.ExistsAsync(cancellationToken))
        {
            _logger.LogInformation("[COST-ANOMALY-STORAGE] Relatório não encontrado: {path}", blobPath);
            return null;
        }

        var downloadResult = await blobClient.DownloadContentAsync(cancellationToken);
        var json = downloadResult.Value.Content.ToString();
        return JsonSerializer.Deserialize<CostAnomalyReport>(json, _jsonOptions);
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
}
