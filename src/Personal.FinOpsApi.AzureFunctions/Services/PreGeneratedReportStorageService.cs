using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public class PreGeneratedReportStorageService
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<PreGeneratedReportStorageService> _logger;

    public PreGeneratedReportStorageService(
        BlobServiceClient blobServiceClient,
        IConfiguration configuration,
        ILogger<PreGeneratedReportStorageService> logger)
    {
        var containerName = configuration["RESULTS_CONTAINER_NAME"] ?? "finops-analysis";
        _container = blobServiceClient.GetBlobContainerClient(containerName);
        _logger = logger;
    }

    public static string BuildGeneralPath(DateTime date)
    {
        return $"reports/{date:yyyy-MM-dd}/general.html";
    }

    public static string BuildSubscriptionPath(DateTime date, string subscriptionId)
    {
        return $"reports/{date:yyyy-MM-dd}/subscriptions/{SanitizePathSegment(subscriptionId)}.html";
    }

    public static string BuildTeamPath(DateTime date, string teamId)
    {
        return $"reports/{date:yyyy-MM-dd}/teams/{SanitizePathSegment(teamId)}.html";
    }

    public async Task SaveHtmlAsync(string blobPath, string htmlContent, CancellationToken cancellationToken = default)
    {
        await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

        var blob = _container.GetBlobClient(blobPath);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(htmlContent));

        await blob.UploadAsync(
            stream,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "text/html; charset=utf-8" }
            },
            cancellationToken);

        _logger.LogInformation("Relatório HTML pré-gerado salvo em {path} ({bytes} bytes)", blobPath, htmlContent.Length);
    }

    public async Task<string?> LoadHtmlAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        var blob = _container.GetBlobClient(blobPath);
        if (!await blob.ExistsAsync(cancellationToken))
        {
            return null;
        }

        var response = await blob.DownloadContentAsync(cancellationToken);
        return response.Value.Content.ToString();
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var safe = new string(value.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray());

        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }
}
