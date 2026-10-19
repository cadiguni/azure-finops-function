using Azure.Storage.Blobs;
using Azure;
using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public class OwnershipStorageService : IOwnershipConfigReader
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly ILogger<OwnershipStorageService> _logger;
    private readonly string _containerName;
    private readonly string _blobPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
    private OwnershipConfig? _cachedConfig;

    public OwnershipStorageService(
        BlobServiceClient blobServiceClient,
        IConfiguration configuration,
        ILogger<OwnershipStorageService> logger)
    {
        _blobServiceClient = blobServiceClient;
        _logger = logger;
        _containerName = configuration["CONFIG_CONTAINER_NAME"] ?? "finops-config";
        _blobPath = configuration["OwnershipConfigBlobPath"] ?? "config/ownership.json";
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public async Task<OwnershipConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        await _loadSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_cachedConfig != null)
            {
                return _cachedConfig;
            }

            var container = _blobServiceClient.GetBlobContainerClient(_containerName);
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            var blob = container.GetBlobClient(_blobPath);
            if (!await blob.ExistsAsync(cancellationToken))
            {
                _logger.LogInformation("[OWNERSHIP] Arquivo não encontrado em {blobPath}, criando estrutura inicial vazia", _blobPath);
                var initial = new OwnershipConfig();
                await SaveConfigInternalAsync(container, initial, "system", createHistoryBackup: false, cancellationToken);
                _cachedConfig = initial;
                return initial;
            }

            var content = await blob.DownloadContentAsync(cancellationToken);
            var config = JsonSerializer.Deserialize<OwnershipConfig>(content.Value.Content.ToString(), _jsonOptions) ?? new OwnershipConfig();
            Normalize(config);
            _cachedConfig = config;
            return config;
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    public async Task SaveConfigAsync(
        OwnershipConfig config,
        string updatedBy,
        bool createHistoryBackup = true,
        CancellationToken cancellationToken = default)
    {
        var container = _blobServiceClient.GetBlobContainerClient(_containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await SaveConfigInternalAsync(container, config, updatedBy, createHistoryBackup, cancellationToken);
        _cachedConfig = config;
    }

    public async Task<TeamOwnership?> GetTeamAsync(string teamId, CancellationToken cancellationToken = default)
    {
        var config = await GetConfigAsync(cancellationToken);
        return config.Teams.FirstOrDefault(t => t.Id.Equals(teamId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _loadSemaphore.WaitAsync(cancellationToken);
        try
        {
            _cachedConfig = null;
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    private async Task SaveConfigInternalAsync(
        BlobContainerClient container,
        OwnershipConfig config,
        string updatedBy,
        bool createHistoryBackup,
        CancellationToken cancellationToken)
    {
        Normalize(config);
        config.Version = config.Version <= 0 ? 1 : config.Version;
        config.UpdatedAt = DateTime.UtcNow;
        config.UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();

        var blob = container.GetBlobClient(_blobPath);
        if (createHistoryBackup && await blob.ExistsAsync(cancellationToken))
        {
            var existing = await blob.DownloadContentAsync(cancellationToken);
            var backupPath = $"config/history/ownership-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json";
            await container.GetBlobClient(backupPath).UploadAsync(
                BinaryData.FromString(existing.Value.Content.ToString()),
                overwrite: true,
                cancellationToken: cancellationToken);
        }

        var json = JsonSerializer.Serialize(config, _jsonOptions);
        await blob.UploadAsync(BinaryData.FromString(json), overwrite: true, cancellationToken: cancellationToken);
    }

    private static void Normalize(OwnershipConfig config)
    {
        config.Teams ??= new List<TeamOwnership>();
        foreach (var team in config.Teams)
        {
            team.Id = team.Id?.Trim() ?? string.Empty;
            team.Name = team.Name?.Trim() ?? string.Empty;
            team.RequiredRole = team.RequiredRole?.Trim() ?? string.Empty;
            team.Source = team.Source?.Trim() ?? "manual";
            team.ManagementGroupIds = team.ManagementGroupIds?
                .Where(managementGroupId => !string.IsNullOrWhiteSpace(managementGroupId))
                .Select(managementGroupId => managementGroupId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            team.Subscriptions ??= new List<SubscriptionOwnership>();

            foreach (var subscription in team.Subscriptions)
            {
                subscription.SubscriptionId = subscription.SubscriptionId?.Trim() ?? string.Empty;
                subscription.SubscriptionName = subscription.SubscriptionName?.Trim() ?? string.Empty;
                subscription.Environment = subscription.Environment?.Trim() ?? string.Empty;
                subscription.Source = subscription.Source?.Trim() ?? "manual";
            }
        }
    }

    public async Task<bool> ConfigExistsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var container = _blobServiceClient.GetBlobContainerClient(_containerName);
            var blob = container.GetBlobClient(_blobPath);
            return await blob.ExistsAsync(cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    public string GetExpectedPath() => $"{_containerName}/{_blobPath}";
}
