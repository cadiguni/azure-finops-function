using Azure.Storage.Blobs;
using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Guarda os ajustes manuais de rateio. Blob, e não Cosmos: é configuração editada por pessoa,
/// de volume pequeno, que segue o mesmo caminho do ownership — inclusive o histórico de versões,
/// que aqui importa porque mudar uma regra reescreve o rateio de todo o período consultado.
/// </summary>
public class CostAllocationRulesStorageService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly ILogger<CostAllocationRulesStorageService> _logger;
    private readonly string _containerName;
    private readonly string _blobPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
    private CostAllocationRulesConfig? _cachedConfig;

    public CostAllocationRulesStorageService(
        BlobServiceClient blobServiceClient,
        IConfiguration configuration,
        ILogger<CostAllocationRulesStorageService> logger)
    {
        _blobServiceClient = blobServiceClient;
        _logger = logger;
        _containerName = configuration["CONFIG_CONTAINER_NAME"] ?? "finops-config";
        _blobPath = configuration["CostAllocationRulesBlobPath"] ?? "config/cost-allocation-rules.json";
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public async Task<CostAllocationRulesConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        await _loadSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_cachedConfig is not null)
            {
                return _cachedConfig;
            }

            var container = _blobServiceClient.GetBlobContainerClient(_containerName);
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            var blob = container.GetBlobClient(_blobPath);
            if (!await blob.ExistsAsync(cancellationToken))
            {
                // Sem arquivo o rateio ainda funciona: cai inteiro na tag. Não criamos o blob aqui
                // para uma leitura não gravar configuração.
                _cachedConfig = new CostAllocationRulesConfig();
                return _cachedConfig;
            }

            var content = await blob.DownloadContentAsync(cancellationToken);
            var config = JsonSerializer.Deserialize<CostAllocationRulesConfig>(
                content.Value.Content.ToString(),
                _jsonOptions) ?? new CostAllocationRulesConfig();
            Normalize(config);
            _cachedConfig = config;
            return config;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "[COST-ALLOCATION] Falha ao ler as regras em {blobPath}. Seguindo apenas com as tags.",
                _blobPath);
            return new CostAllocationRulesConfig();
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    public async Task<IReadOnlyList<CostAllocationRule>> GetRulesAsync(CancellationToken cancellationToken = default)
        => (await GetConfigAsync(cancellationToken)).Rules;

    public async Task SaveConfigAsync(
        CostAllocationRulesConfig config,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        Normalize(config);
        config.Version = config.Version <= 0 ? 1 : config.Version + 1;
        config.UpdatedAt = DateTime.UtcNow;
        config.UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();

        var container = _blobServiceClient.GetBlobContainerClient(_containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var blob = container.GetBlobClient(_blobPath);

        if (await blob.ExistsAsync(cancellationToken))
        {
            var existing = await blob.DownloadContentAsync(cancellationToken);
            var backupPath = $"config/history/cost-allocation-rules-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json";
            await container.GetBlobClient(backupPath).UploadAsync(
                BinaryData.FromString(existing.Value.Content.ToString()),
                overwrite: true,
                cancellationToken: cancellationToken);
        }

        var json = JsonSerializer.Serialize(config, _jsonOptions);
        await blob.UploadAsync(BinaryData.FromString(json), overwrite: true, cancellationToken: cancellationToken);
        _cachedConfig = config;

        _logger.LogInformation(
            "[COST-ALLOCATION] Regras salvas por {updatedBy}. versao={version} total={total}",
            config.UpdatedBy,
            config.Version,
            config.Rules.Count);
    }

    public Task ReloadAsync()
    {
        _cachedConfig = null;
        return Task.CompletedTask;
    }

    public string GetExpectedPath() => $"{_containerName}/{_blobPath}";

    private static void Normalize(CostAllocationRulesConfig config)
    {
        config.Rules ??= [];
        foreach (var rule in config.Rules)
        {
            rule.Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("n") : rule.Id.Trim();
            rule.Type = CostAllocationRuleTypes.Normalize(rule.Type) ?? CostAllocationRuleTypes.ResourceGroup;
            rule.Match = rule.Match?.Trim() ?? string.Empty;
            rule.CostCenter = rule.CostCenter?.Trim() ?? string.Empty;
            rule.SubscriptionId = rule.SubscriptionId?.Trim() ?? string.Empty;
            rule.Note = rule.Note?.Trim() ?? string.Empty;
            rule.CreatedBy = string.IsNullOrWhiteSpace(rule.CreatedBy) ? "system" : rule.CreatedBy.Trim();
            rule.UpdatedBy = string.IsNullOrWhiteSpace(rule.UpdatedBy) ? rule.CreatedBy : rule.UpdatedBy.Trim();
        }
    }
}
