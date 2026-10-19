using Azure.Core;
using Azure.Storage.Blobs;
using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public class SubscriptionBillingCycleService : IBillingPeriodResolver
{
    private const string AzureManagementScope = "https://management.azure.com/.default";
    private const string DefaultBillingPeriodsApiVersion = "2018-03-01-preview";
    private readonly ConcurrentDictionary<string, CachedBillingPeriodInfo> BillingPeriodCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TokenCredential _credential;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpRetryService _httpRetryService;
    private readonly ICostManagementClient _costManagementClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SubscriptionBillingCycleService> _logger;
    private readonly BlobServiceClient? _blobServiceClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _configContainerName;
    private readonly string _overridesBlobPath;
    private readonly string _officialCacheBlobPath;
    private readonly SemaphoreSlim _overrideLoadSemaphore = new(1, 1);
    private readonly SemaphoreSlim _officialCacheLoadSemaphore = new(1, 1);
    private BillingPeriodOverrideConfig? _cachedOverridesConfig;
    private BillingPeriodOfficialCacheConfig? _cachedOfficialCacheConfig;

    public SubscriptionBillingCycleService(
        TokenCredential credential,
        IHttpClientFactory httpClientFactory,
        HttpRetryService httpRetryService,
        ICostManagementClient costManagementClient,
        IConfiguration configuration,
        ILogger<SubscriptionBillingCycleService> logger,
        BlobServiceClient? blobServiceClient = null)
    {
        _credential = credential;
        _httpClientFactory = httpClientFactory;
        _httpRetryService = httpRetryService;
        _costManagementClient = costManagementClient;
        _configuration = configuration;
        _logger = logger;
        _blobServiceClient = blobServiceClient;
        _configContainerName = _configuration["CONFIG_CONTAINER_NAME"] ?? "finops-config";
        _overridesBlobPath = _configuration["BillingPeriodOverridesBlobPath"] ?? "config/billing-period-overrides.json";
        _officialCacheBlobPath = _configuration["BillingPeriodOfficialCacheBlobPath"] ?? "config/billing-period-official-cache.json";
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public async Task<Dictionary<string, SubscriptionBillingCycleWindow>> ResolveCycleWindowsAsync(
        IReadOnlyCollection<string> subscriptionIds,
        DateTime referenceDate,
        CancellationToken cancellationToken = default)
    {
        var resolved = new Dictionary<string, SubscriptionBillingCycleWindow>(StringComparer.OrdinalIgnoreCase);
        if (subscriptionIds.Count == 0)
        {
            return resolved;
        }

        foreach (var subscriptionId in subscriptionIds)
        {
            var info = await ResolveAsync(subscriptionId, referenceDate, cancellationToken);
            resolved[subscriptionId] = FromBillingPeriodInfo(info);
        }

        return resolved;
    }

    public async Task<BillingPeriodInfo> ResolveAsync(
        string subscriptionId,
        DateTime date,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new ArgumentException("SubscriptionId é obrigatório.", nameof(subscriptionId));
        }

        var referenceDate = date.Date;
        var cacheKey = $"{subscriptionId.Trim()}|{referenceDate:yyyy-MM-dd}";
        if (BillingPeriodCache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Info;
        }

        BillingPeriodInfo result;
        var enableAzureSource = _configuration.GetValue("CostAnomalyUseAzureBillingPeriod", true);
        if (enableAzureSource)
        {
            var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { AzureManagementScope }), cancellationToken);
            var azureWindow = await TryGetAzureBillingWindowAsync(subscriptionId, referenceDate, token.Token, cancellationToken);
            if (azureWindow is not null)
            {
                await TryPersistOfficialWindowAsync(subscriptionId, azureWindow, cancellationToken);
                result = ToBillingPeriodInfo(subscriptionId, azureWindow);
                CacheResult(cacheKey, result);
                return result;
            }
        }

        var persistedOfficialWindow = await TryGetPersistedOfficialWindowAsync(subscriptionId, referenceDate, cancellationToken);
        if (persistedOfficialWindow is not null)
        {
            result = ToBillingPeriodInfo(
                subscriptionId,
                new SubscriptionBillingCycleWindow
                {
                    CycleStartDate = persistedOfficialWindow.CycleStartDate,
                    CycleTurnDate = persistedOfficialWindow.CycleTurnDate,
                    Source = BillingPeriodSources.AzureBillingPeriodsApi,
                    Confidence = BillingPeriodConfidence.High,
                    Note = "Billing period oficial recuperado do cache persistido."
                });
            CacheResult(cacheKey, result);
            return result;
        }

        var persistedOverrides = await LoadPersistedTurnDayOverridesAsync(cancellationToken);
        if (persistedOverrides.TryGetValue(subscriptionId, out var persistedOverride))
        {
            result = ToBillingPeriodInfo(
                subscriptionId,
                BuildWindowFromTurnDay(
                    referenceDate,
                    persistedOverride.BillingCycleStartDay,
                    BillingPeriodSources.ManualConfiguration,
                    BillingPeriodConfidence.Medium,
                    $"Billing period configurado manualmente por {persistedOverride.UpdatedBy}."));
            CacheResult(cacheKey, result);
            return result;
        }

        var configuredOverrides = ParseConfiguredTurnDayOverrides();
        if (configuredOverrides.TryGetValue(subscriptionId, out var configuredOverrideTurnDay))
        {
            result = ToBillingPeriodInfo(
                subscriptionId,
                BuildWindowFromTurnDay(
                    referenceDate,
                    configuredOverrideTurnDay,
                    BillingPeriodSources.ManualConfiguration,
                    BillingPeriodConfidence.Medium,
                    "Billing period configurado manualmente via app settings."));
            CacheResult(cacheKey, result);
            return result;
        }

        // O ciclo oficial vira todo mês, então a janela salva deixa de cobrir a data atual e o
        // cache acima falha justamente quando é mais necessário. O dia de virada, porém, é estável:
        // projetá-lo preserva o ciclo real em vez de cair no mês calendário.
        var derivedWindow = await TryDeriveWindowFromPersistedHistoryAsync(
            subscriptionId,
            referenceDate,
            cancellationToken);
        if (derivedWindow is not null)
        {
            result = ToBillingPeriodInfo(subscriptionId, derivedWindow);
            CacheResult(cacheKey, result);
            return result;
        }

        result = ToBillingPeriodInfo(
            subscriptionId,
            BuildCalendarMonthWindow(referenceDate));
        CacheResult(cacheKey, result);
        return result;
    }

    /// <summary>
    /// Projeta o ciclo atual a partir do dia de virada do último billing period oficial conhecido,
    /// quando nenhuma janela persistida cobre a data consultada.
    /// </summary>
    private async Task<SubscriptionBillingCycleWindow?> TryDeriveWindowFromPersistedHistoryAsync(
        string subscriptionId,
        DateTime referenceDate,
        CancellationToken cancellationToken)
    {
        var cache = await LoadOfficialCacheAsync(cancellationToken);
        var derived = DeriveWindowFromHistory(cache.Windows, subscriptionId, referenceDate);
        if (derived is not null)
        {
            _logger.LogInformation(
                "[BILLING-PERIOD] Ciclo de {subscriptionId} projetado a partir do último período oficial conhecido: {from} a {to}.",
                subscriptionId,
                derived.CycleStartDate.ToString("yyyy-MM-dd"),
                derived.CycleTurnDate.AddDays(-1).ToString("yyyy-MM-dd"));
        }

        return derived;
    }

    /// <summary>
    /// Projeta o ciclo vigente pelo dia de virada do último período oficial conhecido. Parte pura,
    /// separada do carregamento do cache para poder ser verificada em teste.
    /// </summary>
    public static SubscriptionBillingCycleWindow? DeriveWindowFromHistory(
        IReadOnlyList<BillingPeriodOfficialWindowInfo> windows,
        string subscriptionId,
        DateTime referenceDate)
    {
        var latest = windows
            .Where(item => item.SubscriptionId.Equals(subscriptionId.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(item => new
            {
                Item = item,
                From = DateTime.TryParseExact(
                    item.CurrentPeriodFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out var parsedFrom)
                    ? parsedFrom.Date
                    : DateTime.MinValue
            })
            .Where(x => x.From != DateTime.MinValue)
            .OrderByDescending(x => x.Item.RetrievedAt)
            .FirstOrDefault();

        if (latest is null)
        {
            return null;
        }

        return BuildWindowFromTurnDay(
            referenceDate,
            latest.From.Day,
            BillingPeriodSources.AzureBillingPeriodsApi,
            BillingPeriodConfidence.Medium,
            $"Ciclo projetado pelo dia {latest.From.Day}, do último billing period oficial conhecido "
                + $"({latest.Item.CurrentPeriodFrom} a {latest.Item.CurrentPeriodTo}).");
    }

    public async Task<IReadOnlyList<BillingPeriodOverrideInfo>> ListOverridesAsync(
        CancellationToken cancellationToken = default)
    {
        var config = await LoadOverridesConfigAsync(cancellationToken);
        return config.Overrides
            .OrderBy(ovr => ovr.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<BillingPeriodOverrideInfo> UpsertOverrideAsync(
        string subscriptionId,
        int billingCycleStartDay,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new ArgumentException("subscriptionId é obrigatório.", nameof(subscriptionId));
        }

        var sanitizedSubscriptionId = subscriptionId.Trim();
        var turnDay = Math.Clamp(billingCycleStartDay, 1, 28);
        var actor = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();

        var config = await LoadOverridesConfigAsync(cancellationToken);
        var existing = config.Overrides.FirstOrDefault(item =>
            item.SubscriptionId.Equals(sanitizedSubscriptionId, StringComparison.OrdinalIgnoreCase));

        var updated = new BillingPeriodOverrideInfo
        {
            SubscriptionId = sanitizedSubscriptionId,
            BillingCycleStartDay = turnDay,
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = actor
        };

        if (existing is null)
        {
            config.Overrides.Add(updated);
        }
        else
        {
            config.Overrides.Remove(existing);
            config.Overrides.Add(updated);
        }

        await SaveOverridesConfigAsync(config, actor, cancellationToken);
        BillingPeriodCache.Clear();
        return updated;
    }

    public async Task<bool> DeleteOverrideAsync(
        string subscriptionId,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new ArgumentException("subscriptionId é obrigatório.", nameof(subscriptionId));
        }

        var sanitizedSubscriptionId = subscriptionId.Trim();
        var actor = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        var config = await LoadOverridesConfigAsync(cancellationToken);
        var existing = config.Overrides.FirstOrDefault(item =>
            item.SubscriptionId.Equals(sanitizedSubscriptionId, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            return false;
        }

        config.Overrides.Remove(existing);
        await SaveOverridesConfigAsync(config, actor, cancellationToken);
        BillingPeriodCache.Clear();
        return true;
    }

    private async Task<SubscriptionBillingCycleWindow?> TryGetAzureBillingWindowAsync(
        string subscriptionId,
        DateTime referenceDate,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        var apiVersion = ResolveBillingPeriodsApiVersion();
        var endpoint = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Billing/billingPeriods?api-version={apiVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var httpClient = _httpClientFactory.CreateClient();
        using var response = await _httpRetryService.SendWithRetryAsync(httpClient, request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation(
                "[BILLING-PERIOD] BillingPeriods API não suportada para subscription {subscriptionId}; usando fallback. apiVersion={apiVersion} status={status} body={body}",
                subscriptionId,
                apiVersion,
                (int)response.StatusCode,
                TruncateForLog(responseBody));
            return null;
        }

        var window = TryParseBillingWindow(responseBody, referenceDate);
        if (window is not null)
        {
            _logger.LogInformation(
                "[BILLING-PERIOD] BillingPeriods resolvido para {subscriptionId}: apiVersion={apiVersion} start={start} turn={turn}",
                subscriptionId,
                apiVersion,
                window.CycleStartDate.ToString("yyyy-MM-dd"),
                window.CycleTurnDate.ToString("yyyy-MM-dd"));
            return window;
        }

        _logger.LogInformation(
            "[BILLING-PERIOD] BillingPeriods retornou 200 para {subscriptionId}, mas sem ciclo válido para {referenceDate}: apiVersion={apiVersion}",
            subscriptionId,
            referenceDate.ToString("yyyy-MM-dd"),
            apiVersion);

        return null;
    }

    private async Task<SubscriptionBillingCycleWindow?> TryInferBillingWindowFromCostManagementAsync(
        string subscriptionId,
        DateTime referenceDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _costManagementClient.QueryCostByServiceForBillingMonthToDateAsync(
                subscriptionId,
                "Daily",
                cancellationToken);

            var cycleStart = response.Rows
                .Where(row => row.UsageDate.HasValue && row.UsageDate.Value.Date <= referenceDate.Date)
                .Select(row => row.UsageDate!.Value.Date)
                .DefaultIfEmpty()
                .Min();

            if (cycleStart == default)
            {
                _logger.LogDebug(
                    "[COST-ANOMALY] BillingMonthToDate sem linhas de custo para {subscriptionId}.",
                    subscriptionId);
                return null;
            }

            var cycleTurn = BuildNextTurnDate(cycleStart);
            if (referenceDate.Date >= cycleTurn)
            {
                return null;
            }

            return new SubscriptionBillingCycleWindow
            {
                CycleStartDate = cycleStart,
                CycleTurnDate = cycleTurn,
                Source = CurrentCycleSources.CostManagementBillingMonthToDate
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "[COST-ANOMALY] Não foi possível inferir ciclo via Cost Management BillingMonthToDate para {subscriptionId}.",
                subscriptionId);
            return null;
        }
    }

    private static SubscriptionBillingCycleWindow? TryParseBillingWindow(string json, DateTime referenceDate)
    {
        using var document = JsonDocument.Parse(json);
        if (!TryGetPropertyIgnoreCase(document.RootElement, "value", out var valuesElement) ||
            valuesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var periods = new List<(DateTime Start, DateTime End)>();
        foreach (var element in valuesElement.EnumerateArray())
        {
            if (!TryGetPropertyIgnoreCase(element, "properties", out var propertiesElement))
            {
                continue;
            }

            if (!TryReadDateProperty(propertiesElement, "billingPeriodStartDate", out var startDate) ||
                !TryReadDateProperty(propertiesElement, "billingPeriodEndDate", out var endDate))
            {
                continue;
            }

            periods.Add((startDate.Date, endDate.Date));
        }

        var current = periods.FirstOrDefault(period =>
            period.Start != default &&
            period.End != default &&
            period.Start <= referenceDate.Date &&
            referenceDate.Date <= period.End);

        if (current == default)
        {
            return null;
        }

        return new SubscriptionBillingCycleWindow
        {
            CycleStartDate = current.Start,
            CycleTurnDate = current.End.AddDays(1),
            Source = BillingPeriodSources.AzureBillingPeriodsApi,
            Confidence = BillingPeriodConfidence.High,
            Note = "Billing period oficial retornado pela Azure BillingPeriods API."
        };
    }

    private async Task<Dictionary<string, BillingPeriodOverrideInfo>> LoadPersistedTurnDayOverridesAsync(
        CancellationToken cancellationToken)
    {
        var config = await LoadOverridesConfigAsync(cancellationToken);
        return config.Overrides
            .GroupBy(overrideItem => overrideItem.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.UpdatedAt).First(),
                StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, int> ParseConfiguredTurnDayOverrides()
    {
        var raw = _configuration["CostAnomalySubscriptionTurnDayOverrides"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var entries = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in entries)
        {
            var pair = entry.Split('=', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]))
            {
                continue;
            }

            if (!int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var turnDay))
            {
                continue;
            }

            result[pair[0]] = Math.Clamp(turnDay, 1, 28);
        }

        return result;
    }

    private async Task<BillingPeriodOverrideConfig> LoadOverridesConfigAsync(CancellationToken cancellationToken)
    {
        if (_cachedOverridesConfig is not null)
        {
            return _cachedOverridesConfig;
        }

        if (_blobServiceClient is null)
        {
            _cachedOverridesConfig = new BillingPeriodOverrideConfig();
            return _cachedOverridesConfig;
        }

        await _overrideLoadSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_cachedOverridesConfig is not null)
            {
                return _cachedOverridesConfig;
            }

            var container = _blobServiceClient.GetBlobContainerClient(_configContainerName);
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            var blob = container.GetBlobClient(_overridesBlobPath);
            if (!await blob.ExistsAsync(cancellationToken))
            {
                _cachedOverridesConfig = new BillingPeriodOverrideConfig();
                return _cachedOverridesConfig;
            }

            var content = await blob.DownloadContentAsync(cancellationToken);
            var parsed = JsonSerializer.Deserialize<BillingPeriodOverrideConfig>(
                content.Value.Content.ToString(),
                _jsonOptions) ?? new BillingPeriodOverrideConfig();
            NormalizeOverridesConfig(parsed);
            _cachedOverridesConfig = parsed;
            return _cachedOverridesConfig;
        }
        finally
        {
            _overrideLoadSemaphore.Release();
        }
    }

    private async Task SaveOverridesConfigAsync(
        BillingPeriodOverrideConfig config,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        if (_blobServiceClient is null)
        {
            throw new InvalidOperationException("BlobServiceClient não configurado para persistir overrides de billing.");
        }

        NormalizeOverridesConfig(config);
        config.Version = config.Version <= 0 ? 1 : config.Version;
        config.UpdatedAt = DateTime.UtcNow;
        config.UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        config.Overrides = config.Overrides
            .OrderBy(ovr => ovr.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var container = _blobServiceClient.GetBlobContainerClient(_configContainerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var blob = container.GetBlobClient(_overridesBlobPath);
        var json = JsonSerializer.Serialize(config, _jsonOptions);
        await blob.UploadAsync(BinaryData.FromString(json), overwrite: true, cancellationToken: cancellationToken);
        _cachedOverridesConfig = config;
    }

    private static void NormalizeOverridesConfig(BillingPeriodOverrideConfig config)
    {
        config.Overrides ??= new List<BillingPeriodOverrideInfo>();
        var normalized = new List<BillingPeriodOverrideInfo>();
        foreach (var entry in config.Overrides)
        {
            if (string.IsNullOrWhiteSpace(entry.SubscriptionId))
            {
                continue;
            }

            normalized.Add(new BillingPeriodOverrideInfo
            {
                SubscriptionId = entry.SubscriptionId.Trim(),
                BillingCycleStartDay = Math.Clamp(entry.BillingCycleStartDay, 1, 28),
                UpdatedAt = entry.UpdatedAt == default ? DateTime.UtcNow : entry.UpdatedAt,
                UpdatedBy = string.IsNullOrWhiteSpace(entry.UpdatedBy) ? "unknown" : entry.UpdatedBy.Trim()
            });
        }

        config.Overrides = normalized
            .GroupBy(entry => entry.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(entry => entry.UpdatedAt).First())
            .ToList();
    }

    private async Task TryPersistOfficialWindowAsync(
        string subscriptionId,
        SubscriptionBillingCycleWindow azureWindow,
        CancellationToken cancellationToken)
    {
        if (_blobServiceClient is null)
        {
            return;
        }

        try
        {
            var cache = await LoadOfficialCacheAsync(cancellationToken);
            var periodFrom = azureWindow.CycleStartDate.ToString("yyyy-MM-dd");
            var periodTo = azureWindow.CycleTurnDate.AddDays(-1).ToString("yyyy-MM-dd");
            var sanitizedSubscriptionId = subscriptionId.Trim();
            cache.Windows = cache.Windows
                .Where(item =>
                    !item.SubscriptionId.Equals(sanitizedSubscriptionId, StringComparison.OrdinalIgnoreCase)
                    || !item.CurrentPeriodFrom.Equals(periodFrom, StringComparison.OrdinalIgnoreCase)
                    || !item.CurrentPeriodTo.Equals(periodTo, StringComparison.OrdinalIgnoreCase))
                .ToList();
            cache.Windows.Add(new BillingPeriodOfficialWindowInfo
            {
                SubscriptionId = sanitizedSubscriptionId,
                CurrentPeriodFrom = periodFrom,
                CurrentPeriodTo = periodTo,
                RetrievedAt = DateTime.UtcNow,
                RetrievedBySource = BillingPeriodSources.AzureBillingPeriodsApi
            });

            await SaveOfficialCacheAsync(cache, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[BILLING-PERIOD] Falha ao persistir cache oficial para {subscriptionId}.",
                subscriptionId);
        }
    }

    private async Task<SubscriptionBillingCycleWindow?> TryGetPersistedOfficialWindowAsync(
        string subscriptionId,
        DateTime referenceDate,
        CancellationToken cancellationToken)
    {
        var cache = await LoadOfficialCacheAsync(cancellationToken);
        var candidate = cache.Windows
            .Where(item => item.SubscriptionId.Equals(subscriptionId.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(item => new
            {
                Item = item,
                From = DateTime.TryParseExact(item.CurrentPeriodFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedFrom)
                    ? parsedFrom.Date
                    : DateTime.MinValue,
                To = DateTime.TryParseExact(item.CurrentPeriodTo, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTo)
                    ? parsedTo.Date
                    : DateTime.MinValue
            })
            .Where(x =>
                x.From != DateTime.MinValue &&
                x.To != DateTime.MinValue &&
                x.From <= referenceDate.Date &&
                referenceDate.Date <= x.To)
            .OrderByDescending(x => x.Item.RetrievedAt)
            .FirstOrDefault();

        if (candidate is null)
        {
            return null;
        }

        return new SubscriptionBillingCycleWindow
        {
            CycleStartDate = candidate.From,
            CycleTurnDate = candidate.To.AddDays(1),
            Source = BillingPeriodSources.AzureBillingPeriodsApi,
            Confidence = BillingPeriodConfidence.High,
            Note = "Billing period oficial recuperado do cache persistido."
        };
    }

    private async Task<BillingPeriodOfficialCacheConfig> LoadOfficialCacheAsync(CancellationToken cancellationToken)
    {
        if (_cachedOfficialCacheConfig is not null)
        {
            return _cachedOfficialCacheConfig;
        }

        if (_blobServiceClient is null)
        {
            _cachedOfficialCacheConfig = new BillingPeriodOfficialCacheConfig();
            return _cachedOfficialCacheConfig;
        }

        await _officialCacheLoadSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_cachedOfficialCacheConfig is not null)
            {
                return _cachedOfficialCacheConfig;
            }

            var container = _blobServiceClient.GetBlobContainerClient(_configContainerName);
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            var blob = container.GetBlobClient(_officialCacheBlobPath);
            if (!await blob.ExistsAsync(cancellationToken))
            {
                _cachedOfficialCacheConfig = new BillingPeriodOfficialCacheConfig();
                return _cachedOfficialCacheConfig;
            }

            var content = await blob.DownloadContentAsync(cancellationToken);
            var parsed = JsonSerializer.Deserialize<BillingPeriodOfficialCacheConfig>(
                content.Value.Content.ToString(),
                _jsonOptions) ?? new BillingPeriodOfficialCacheConfig();
            NormalizeOfficialCache(parsed);
            _cachedOfficialCacheConfig = parsed;
            return _cachedOfficialCacheConfig;
        }
        finally
        {
            _officialCacheLoadSemaphore.Release();
        }
    }

    private async Task SaveOfficialCacheAsync(
        BillingPeriodOfficialCacheConfig config,
        CancellationToken cancellationToken)
    {
        if (_blobServiceClient is null)
        {
            return;
        }

        NormalizeOfficialCache(config);
        config.Version = config.Version <= 0 ? 1 : config.Version;
        config.UpdatedAt = DateTime.UtcNow;

        var container = _blobServiceClient.GetBlobContainerClient(_configContainerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var blob = container.GetBlobClient(_officialCacheBlobPath);
        var json = JsonSerializer.Serialize(config, _jsonOptions);
        await blob.UploadAsync(BinaryData.FromString(json), overwrite: true, cancellationToken: cancellationToken);
        _cachedOfficialCacheConfig = config;
    }

    private static void NormalizeOfficialCache(BillingPeriodOfficialCacheConfig config)
    {
        config.Windows ??= new List<BillingPeriodOfficialWindowInfo>();
        var normalized = new List<BillingPeriodOfficialWindowInfo>();
        foreach (var window in config.Windows)
        {
            if (string.IsNullOrWhiteSpace(window.SubscriptionId))
            {
                continue;
            }

            if (!DateTime.TryParseExact(window.CurrentPeriodFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedFrom))
            {
                continue;
            }

            if (!DateTime.TryParseExact(window.CurrentPeriodTo, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTo))
            {
                continue;
            }

            if (parsedTo.Date < parsedFrom.Date)
            {
                continue;
            }

            normalized.Add(new BillingPeriodOfficialWindowInfo
            {
                SubscriptionId = window.SubscriptionId.Trim(),
                CurrentPeriodFrom = parsedFrom.ToString("yyyy-MM-dd"),
                CurrentPeriodTo = parsedTo.ToString("yyyy-MM-dd"),
                RetrievedAt = window.RetrievedAt == default ? DateTime.UtcNow : window.RetrievedAt,
                RetrievedBySource = string.IsNullOrWhiteSpace(window.RetrievedBySource)
                    ? BillingPeriodSources.AzureBillingPeriodsApi
                    : window.RetrievedBySource.Trim()
            });
        }

        config.Windows = normalized
            .GroupBy(item => $"{item.SubscriptionId}|{item.CurrentPeriodFrom}|{item.CurrentPeriodTo}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.RetrievedAt).First())
            .GroupBy(item => item.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.OrderByDescending(item => item.RetrievedAt).Take(6))
            .OrderBy(item => item.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.RetrievedAt)
            .ToList();
    }

    private string ResolveBillingPeriodsApiVersion()
    {
        return _configuration["CostAnomalyBillingPeriodsApiVersion"]
            ?? _configuration["BillingPeriodsApiVersion"]
            ?? DefaultBillingPeriodsApiVersion;
    }

    private static string TruncateForLog(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var singleLine = value.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= 500 ? singleLine : string.Concat(singleLine.AsSpan(0, 500), "...");
    }

    private static SubscriptionBillingCycleWindow BuildWindowFromTurnDay(
        DateTime referenceDate,
        int turnDay,
        string source,
        string confidence,
        string note)
    {
        var thisMonthTurn = BuildTurnDate(referenceDate.Year, referenceDate.Month, turnDay);
        DateTime cycleStart;
        DateTime cycleTurn;
        if (referenceDate.Date >= thisMonthTurn)
        {
            cycleStart = thisMonthTurn;
            var nextMonth = referenceDate.AddMonths(1);
            cycleTurn = BuildTurnDate(nextMonth.Year, nextMonth.Month, turnDay);
        }
        else
        {
            var previousMonth = referenceDate.AddMonths(-1);
            cycleStart = BuildTurnDate(previousMonth.Year, previousMonth.Month, turnDay);
            cycleTurn = thisMonthTurn;
        }

        return new SubscriptionBillingCycleWindow
        {
            CycleStartDate = cycleStart,
            CycleTurnDate = cycleTurn,
            Source = source,
            Confidence = confidence,
            Note = note
        };
    }

    private static SubscriptionBillingCycleWindow BuildCalendarMonthWindow(DateTime referenceDate)
    {
        var cycleStart = new DateTime(referenceDate.Year, referenceDate.Month, 1);
        return new SubscriptionBillingCycleWindow
        {
            CycleStartDate = cycleStart,
            CycleTurnDate = cycleStart.AddMonths(1),
            Source = BillingPeriodSources.DefaultCalendarMonth,
            Confidence = BillingPeriodConfidence.Low,
            Note = "Billing period oficial não encontrado; usando mês calendário."
        };
    }

    private static BillingPeriodInfo ToBillingPeriodInfo(string subscriptionId, SubscriptionBillingCycleWindow window)
    {
        return new BillingPeriodInfo
        {
            SubscriptionId = subscriptionId,
            CurrentPeriodFrom = window.CycleStartDate.ToString("yyyy-MM-dd"),
            CurrentPeriodTo = window.CycleTurnDate.AddDays(-1).ToString("yyyy-MM-dd"),
            BillingCycleStartDay = window.CycleStartDate.Day,
            Source = window.Source,
            Confidence = window.Confidence,
            Note = window.Note
        };
    }

    private static SubscriptionBillingCycleWindow FromBillingPeriodInfo(BillingPeriodInfo info)
    {
        var periodFrom = DateTime.ParseExact(info.CurrentPeriodFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture).Date;
        var periodTo = DateTime.ParseExact(info.CurrentPeriodTo, "yyyy-MM-dd", CultureInfo.InvariantCulture).Date;
        return new SubscriptionBillingCycleWindow
        {
            CycleStartDate = periodFrom,
            CycleTurnDate = periodTo.AddDays(1),
            Source = info.Source,
            Confidence = info.Confidence,
            Note = info.Note
        };
    }

    private void CacheResult(string cacheKey, BillingPeriodInfo result)
    {
        BillingPeriodCache[cacheKey] = new CachedBillingPeriodInfo(
            result,
            DateTimeOffset.UtcNow.AddHours(4));
    }

    private readonly record struct CachedBillingPeriodInfo(
        BillingPeriodInfo Info,
        DateTimeOffset ExpiresAt);

    private static DateTime BuildTurnDate(int year, int month, int turnDay)
    {
        var day = Math.Min(turnDay, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, day);
    }

    private static DateTime BuildNextTurnDate(DateTime cycleStart)
    {
        var nextMonth = cycleStart.AddMonths(1);
        return BuildTurnDate(nextMonth.Year, nextMonth.Month, cycleStart.Day);
    }

    private static bool TryReadDateProperty(JsonElement element, string propertyName, out DateTime date)
    {
        date = default;
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsedDate))
        {
            date = parsedDate;
            return true;
        }

        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
