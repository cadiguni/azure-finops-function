using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Personal.FinOpsApi.AzureFunctions.Services
{
    /// <summary>
    /// Dados de custo de um recurso (diário e mensal + origem/confiança do match)
    /// </summary>
    public record ResourceCostData(
        decimal DailyCost,
        decimal MonthlyCost,
        string Currency = "BRL",
        DateTime? CostPeriodFrom = null,
        DateTime? CostPeriodTo = null,
        string CostSource = CostMatchSources.NotMatched,
        string CostMatchConfidence = CostMatchConfidenceLevels.Low,
        string? CostMatchedResourceId = null,
        string? CostNote = "Não foi possível associar custo para este recurso no período consultado.",
        CostReadProvenance? Provenance = null,
        decimal? ObservedCost = null);

    /// <summary>
    /// Lookup por recurso usando a leitura compartilhada Export-first, com fallback controlado.
    /// Mantém cache por subscription em lote e resolve match em memória.
    /// </summary>
    public class ResourceCostLookupService
    {
        private readonly ICostManagementClient _costManagementClient;
        private readonly ICostDataReader _costDataReader;
        private readonly IResourceCostResolver _resourceCostResolver;
        private readonly ILogger<ResourceCostLookupService> _logger;

        private readonly ConcurrentDictionary<string, SubscriptionCostCache> _costCache = new();
        private readonly ConcurrentDictionary<string, DateTime> _cacheTimestamps = new();
        private readonly SemaphoreSlim _loadSemaphore = new(1, 1);

        private const int CacheExpirationMinutes = 30;
        private const int EmptyCacheExpirationMinutes = 2;

        public ResourceCostLookupService(
            ICostManagementClient costManagementClient,
            ICostDataReader costDataReader,
            IResourceCostResolver resourceCostResolver,
            ILogger<ResourceCostLookupService> logger)
        {
            _costManagementClient = costManagementClient;
            _costDataReader = costDataReader;
            _resourceCostResolver = resourceCostResolver;
            _logger = logger;
        }

        /// <summary>
        /// Obtém o custo mensal estimado de um recurso baseado em dados reais do Cost Management
        /// </summary>
        /// <param name="subscriptionId">ID da subscription</param>
        /// <param name="resourceId">Resource ID completo</param>
        /// <returns>Custo mensal estimado em BRL (ou 0 se não encontrado)</returns>
        public async Task<decimal> GetResourceMonthlyCostAsync(string subscriptionId, string resourceId)
        {
            var costData = await GetResourceCostDataAsync(subscriptionId, resourceId);
            return costData.MonthlyCost;
        }

        /// <summary>
        /// Obtém os custos diário e mensal de um recurso
        /// </summary>
        /// <param name="subscriptionId">ID da subscription</param>
        /// <param name="resourceId">Resource ID completo</param>
        /// <returns>ResourceCostData com custo diário/mensal e metadados de match</returns>
        public async Task<ResourceCostData> GetResourceCostDataAsync(string subscriptionId, string resourceId, string? billingParentResourceId = null)
        {
            if (string.IsNullOrEmpty(subscriptionId) || string.IsNullOrEmpty(resourceId))
                return new ResourceCostData(0, 0);

            var period = GetDefaultPeriod();
            var resolution = await ResolveCostAsync(subscriptionId, resourceId, null, null, period.FromUtc, period.ToUtc, useCache: true, billingParentResourceId: billingParentResourceId);
            return MapToCostData(resolution);
        }

        public async Task<ResourceCostData> GetResourceCostDataAsync(
            string subscriptionId,
            string resourceId,
            string? resourceType,
            string? resourceGroup,
            DateTime periodFromUtc,
            DateTime periodToUtc,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(subscriptionId) || string.IsNullOrEmpty(resourceId))
                return new ResourceCostData(0, 0);

            var resolution = await ResolveCostAsync(
                subscriptionId,
                resourceId,
                resourceType,
                resourceGroup,
                periodFromUtc,
                periodToUtc,
                useCache: false,
                cancellationToken: cancellationToken);
            return MapToCostData(resolution);
        }

        public async Task<ResourceCostResolution> GetCostMatchDiagnosticsAsync(
            string resourceId,
            string periodId,
            CancellationToken cancellationToken = default)
        {
            var normalizedResourceId = _resourceCostResolver.NormalizeResourceId(resourceId);
            var subscriptionId = ExtractSubscriptionId(normalizedResourceId);
            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                return new ResourceCostResolution
                {
                    PeriodFromUtc = DateTime.UtcNow.Date.AddDays(-30),
                    PeriodToUtc = DateTime.UtcNow.Date,
                    CostSource = CostMatchSources.NotMatched,
                    CostMatchConfidence = CostMatchConfidenceLevels.Low,
                    CostNote = "Não foi possível extrair subscriptionId do resourceId.",
                    Observations = new[] { "ResourceId inválido para diagnóstico." }
                };
            }

            var period = ParsePeriod(periodId);
            return await ResolveCostAsync(
                subscriptionId,
                normalizedResourceId,
                ExtractResourceType(normalizedResourceId),
                ExtractResourceGroup(normalizedResourceId),
                period.FromUtc,
                period.ToUtc,
                useCache: false,
                cancellationToken: cancellationToken);
        }

        public async Task<IReadOnlyCollection<CostDimensionInfo>> GetAvailableDimensionsAsync(
            string subscriptionId,
            CancellationToken cancellationToken = default)
        {
            return await _costManagementClient.GetAvailableDimensionsAsync(subscriptionId, cancellationToken);
        }

        public async Task<IReadOnlyCollection<CostDiagnosticRow>> QueryDiagnosticsRowsAsync(
            string subscriptionId,
            DateTime periodFromUtc,
            DateTime periodToUtc,
            CancellationToken cancellationToken = default)
        {
            var result = await _costDataReader.ReadResourceCostsAsync(subscriptionId, periodFromUtc, periodToUtc, cancellationToken);
            return result.Rows;
        }

        private async Task<ResourceCostResolution> ResolveCostAsync(
            string subscriptionId,
            string resourceId,
            string? resourceType,
            string? resourceGroup,
            DateTime periodFromUtc,
            DateTime periodToUtc,
            bool useCache,
            CancellationToken cancellationToken = default,
            string? billingParentResourceId = null)
        {
            ResourceCostReadResult read;
            if (useCache)
            {
                await EnsureCostsLoadedAsync(subscriptionId, cancellationToken);
                read = _costCache.TryGetValue(subscriptionId.ToLowerInvariant(), out var cache)
                    ? cache.ReadResult
                    : new ResourceCostReadResult();
            }
            else
            {
                read = await _costDataReader.ReadResourceCostsAsync(subscriptionId, periodFromUtc, periodToUtc, cancellationToken);
            }

            var context = new ResourceCostResolveContext
            {
                ResourceId = resourceId,
                BillingParentResourceId = billingParentResourceId,
                ResourceType = string.IsNullOrWhiteSpace(resourceType) ? ExtractResourceType(resourceId) : resourceType,
                ResourceGroup = string.IsNullOrWhiteSpace(resourceGroup) ? ExtractResourceGroup(resourceId) : resourceGroup,
                SubscriptionId = subscriptionId
            };

            var resolution = _resourceCostResolver.Resolve(context, read.Rows, periodFromUtc, periodToUtc);
            resolution.Provenance = read.Provenance;
            return resolution;
        }

        /// <summary>
        /// Obtém custos de múltiplos recursos de uma vez
        /// </summary>
        public async Task<Dictionary<string, ResourceCostData>> GetResourceCostsAsync(string subscriptionId, IEnumerable<string> resourceIds)
        {
            await EnsureCostsLoadedAsync(subscriptionId);

            var results = new Dictionary<string, ResourceCostData>(StringComparer.OrdinalIgnoreCase);
            var period = GetDefaultPeriod();
            if (_costCache.TryGetValue(subscriptionId.ToLowerInvariant(), out var cache))
            {
                foreach (var resourceId in resourceIds)
                {
                    var context = new ResourceCostResolveContext
                    {
                        ResourceId = resourceId,
                        ResourceType = ExtractResourceType(resourceId),
                        ResourceGroup = ExtractResourceGroup(resourceId),
                        SubscriptionId = subscriptionId
                    };
                    var resolution = _resourceCostResolver.Resolve(context, cache.ReadResult.Rows, period.FromUtc, period.ToUtc);
                    resolution.Provenance = cache.ReadResult.Provenance;
                    results[resourceId] = MapToCostData(resolution);
                }
            }

            return results;
        }

        /// <summary>
        /// Pré-carrega custos para uma subscription (útil antes de análise batch)
        /// </summary>
        public async Task PreloadCostsAsync(string subscriptionId)
        {
            await EnsureCostsLoadedAsync(subscriptionId);
        }

        /// <summary>
        /// Limpa o cache
        /// </summary>
        public void ClearCache()
        {
            _costCache.Clear();
            _cacheTimestamps.Clear();
            _logger.LogInformation("💰 Cache de custos limpo");
        }

        private async Task EnsureCostsLoadedAsync(string subscriptionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subscriptionIdLower = subscriptionId.ToLowerInvariant();

            // Verificar se já está em cache e não expirou
            if (_cacheTimestamps.TryGetValue(subscriptionIdLower, out var timestamp))
            {
                if (DateTime.UtcNow - timestamp < TimeSpan.FromMinutes(CacheExpirationMinutes)
                    && _costCache.TryGetValue(subscriptionIdLower, out var cached) && cached.PeriodToUtc == GetDefaultPeriod().ToUtc)
                {
                    return; // Cache ainda válido
                }
            }

            await _loadSemaphore.WaitAsync(cancellationToken);
            try
            {
                // Double-check após obter o lock
                if (_cacheTimestamps.TryGetValue(subscriptionIdLower, out timestamp))
                {
                    if (DateTime.UtcNow - timestamp < TimeSpan.FromMinutes(CacheExpirationMinutes)
                        && _costCache.TryGetValue(subscriptionIdLower, out var cached) && cached.PeriodToUtc == GetDefaultPeriod().ToUtc)
                    {
                        return;
                    }
                }

                _logger.LogInformation("💰 Carregando custos Export-first para subscription {SubscriptionId}", subscriptionId);

                var period = GetDefaultPeriod();
                var read = await _costDataReader.ReadResourceCostsAsync(subscriptionId, period.FromUtc, period.ToUtc, cancellationToken);
                var rows = read.Rows;

                var costsByResource = new Dictionary<string, ResourceCostData>(StringComparer.OrdinalIgnoreCase);
                var resourceGroups = rows
                    .Where(r => !string.IsNullOrWhiteSpace(r.ResourceId))
                    .GroupBy(r => _resourceCostResolver.NormalizeResourceId(r.ResourceId));

                foreach (var group in resourceGroups)
                {
                    var context = new ResourceCostResolveContext
                    {
                        ResourceId = group.Key,
                        ResourceType = group.First().ResourceType,
                        ResourceGroup = group.First().ResourceGroupName,
                        SubscriptionId = subscriptionId
                    };
                    var resolution = _resourceCostResolver.Resolve(context, rows, period.FromUtc, period.ToUtc);
                    resolution.Provenance = read.Provenance;
                    costsByResource[group.Key] = MapToCostData(resolution);
                }

                _costCache[subscriptionIdLower] = new SubscriptionCostCache
                {
                    ReadResult = read,
                    CostsByNormalizedResourceId = costsByResource,
                    PeriodFromUtc = period.FromUtc,
                    PeriodToUtc = period.ToUtc
                };
                _cacheTimestamps[subscriptionIdLower] = rows.Count == 0
                    ? DateTime.UtcNow.AddMinutes(-(CacheExpirationMinutes - EmptyCacheExpirationMinutes))
                    : DateTime.UtcNow;

                LogSqlAndCosmosDiagnostics(subscriptionId, rows);

                _logger.LogInformation("💰 Carregados custos de {Count} recursos para subscription {SubscriptionId}. Total mensal projetado: R$ {Total:N2}",
                    costsByResource.Count, subscriptionId, costsByResource.Values.Sum(c => c.MonthlyCost));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "💰 Falha nas fontes de custo para {SubscriptionId}. Custos indisponíveis.", subscriptionId);
                // Evita uma rajada imediata de novas consultas, sem congelar a falha por 30 minutos.
                _costCache[subscriptionIdLower] = new SubscriptionCostCache
                {
                    ReadResult = new ResourceCostReadResult
                    {
                        Provenance = new CostReadProvenance
                        {
                            Source = "Unavailable", FallbackReason = ex.Message,
                            RequestedFrom = GetDefaultPeriod().FromUtc, RequestedTo = GetDefaultPeriod().ToUtc
                        }
                    },
                    CostsByNormalizedResourceId = new Dictionary<string, ResourceCostData>(StringComparer.OrdinalIgnoreCase),
                    PeriodFromUtc = DateTime.UtcNow.Date.AddDays(-30),
                    PeriodToUtc = DateTime.UtcNow.Date
                };
                _cacheTimestamps[subscriptionIdLower] =
                    DateTime.UtcNow.AddMinutes(-(CacheExpirationMinutes - EmptyCacheExpirationMinutes));
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }

        private static ResourceCostData MapToCostData(ResourceCostResolution resolution)
        {
            var costNote = string.Equals(resolution.Provenance?.Source, "Unavailable", StringComparison.OrdinalIgnoreCase)
                ? "Custo temporariamente indisponível: o Export e o fallback da Query API falharam. O último valor válido será preservado."
                : resolution.CostNote;
            return new ResourceCostData(
                resolution.DailyCost,
                resolution.MonthlyCost,
                resolution.Currency,
                resolution.PeriodFromUtc,
                resolution.PeriodToUtc,
                resolution.CostSource,
                resolution.CostMatchConfidence,
                resolution.CostMatchedResourceId,
                costNote,
                resolution.Provenance,
                resolution.BestMatch?.TotalCost);
        }

        private static CostQueryPeriod GetDefaultPeriod()
        {
            var to = DateTime.UtcNow.Date;
            return new CostQueryPeriod
            {
                FromUtc = to.AddDays(-30),
                ToUtc = to
            };
        }

        private static CostQueryPeriod ParsePeriod(string? periodId)
        {
            if (string.IsNullOrWhiteSpace(periodId))
            {
                return GetDefaultPeriod();
            }

            // A month identifier has no timezone. AssumeUniversal may shift midnight to the prior month locally.
            if (DateTime.TryParseExact(periodId, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var month))
            {
                var from = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                return new CostQueryPeriod
                {
                    FromUtc = from,
                    ToUtc = from.AddMonths(1).AddDays(-1)
                };
            }

            var parts = periodId.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && DateTime.TryParse(parts[0], out var fromDate)
                && DateTime.TryParse(parts[1], out var toDate))
            {
                return new CostQueryPeriod
                {
                    FromUtc = fromDate.Date,
                    ToUtc = toDate.Date
                };
            }

            return GetDefaultPeriod();
        }

        private static string ExtractSubscriptionId(string resourceId)
        {
            var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Equals("subscriptions", StringComparison.OrdinalIgnoreCase))
                {
                    return parts[i + 1];
                }
            }

            return string.Empty;
        }

        private static string ExtractResourceGroup(string resourceId)
        {
            var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
                {
                    return parts[i + 1];
                }
            }

            return string.Empty;
        }

        private static string ExtractResourceType(string resourceId)
        {
            var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var providersIndex = Array.FindIndex(parts, p => p.Equals("providers", StringComparison.OrdinalIgnoreCase));
            if (providersIndex < 0 || providersIndex + 2 >= parts.Length)
            {
                return string.Empty;
            }

            var typeParts = new List<string> { parts[providersIndex + 1] };
            for (var i = providersIndex + 2; i < parts.Length; i += 2)
            {
                typeParts.Add(parts[i]);
            }

            return string.Join('/', typeParts);
        }


        private void LogSqlAndCosmosDiagnostics(string subscriptionId, IReadOnlyCollection<CostDiagnosticRow> rows)
        {
            var sqlRows = rows.Where(r =>
                    ContainsAny(r.ResourceType, "microsoft.sql")
                    || ContainsAny(r.ServiceName, "sql")
                    || ContainsAny(r.MeterCategory, "sql")
                    || ContainsAny(r.MeterSubCategory, "sql database"))
                .Take(20)
                .ToList();

            var cosmosRows = rows.Where(r =>
                    ContainsAny(r.ResourceType, "microsoft.documentdb")
                    || ContainsAny(r.ServiceName, "cosmos", "documentdb")
                    || ContainsAny(r.MeterCategory, "cosmos", "documentdb")
                    || ContainsAny(r.MeterSubCategory, "cosmos", "documentdb"))
                .Take(20)
                .ToList();

            _logger.LogInformation(
                "[COST-DIAG] SQL candidates ({SubscriptionId}): {Count}",
                subscriptionId,
                sqlRows.Count);
            foreach (var row in sqlRows)
            {
                _logger.LogInformation(
                    "[COST-DIAG] SQL row -> resourceId={ResourceId}, resourceType={ResourceType}, rg={Rg}, service={ServiceName}, meter={MeterCategory}/{MeterSubCategory}, cost={Cost:N2} {Currency}",
                    row.ResourceId,
                    row.ResourceType,
                    row.ResourceGroupName,
                    row.ServiceName,
                    row.MeterCategory,
                    row.MeterSubCategory,
                    row.TotalCost,
                    row.Currency);
            }

            _logger.LogInformation(
                "[COST-DIAG] Cosmos candidates ({SubscriptionId}): {Count}",
                subscriptionId,
                cosmosRows.Count);
            foreach (var row in cosmosRows)
            {
                _logger.LogInformation(
                    "[COST-DIAG] Cosmos row -> resourceId={ResourceId}, resourceType={ResourceType}, rg={Rg}, service={ServiceName}, meter={MeterCategory}/{MeterSubCategory}, cost={Cost:N2} {Currency}",
                    row.ResourceId,
                    row.ResourceType,
                    row.ResourceGroupName,
                    row.ServiceName,
                    row.MeterCategory,
                    row.MeterSubCategory,
                    row.TotalCost,
                    row.Currency);
            }
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            foreach (var term in terms)
            {
                if (value.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class SubscriptionCostCache
        {
            public ResourceCostReadResult ReadResult { get; init; } = new();
            public Dictionary<string, ResourceCostData> CostsByNormalizedResourceId { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public DateTime PeriodFromUtc { get; init; }
            public DateTime PeriodToUtc { get; init; }
        }
    }
}
