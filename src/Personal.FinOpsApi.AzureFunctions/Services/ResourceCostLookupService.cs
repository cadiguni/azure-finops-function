using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Personal.FinOpsApi.AzureFunctions.Services
{
    /// <summary>
    /// Dados de custo de um recurso (diário e mensal)
    /// </summary>
    public record ResourceCostData(decimal DailyCost, decimal MonthlyCost);

    /// <summary>
    /// Serviço para lookup de custos reais por recurso usando Cost Management API
    /// Faz cache dos custos por subscription para evitar múltiplas chamadas
    /// </summary>
    public class ResourceCostLookupService
    {
        private readonly ICostManagementClient _costManagementClient;
        private readonly ILogger<ResourceCostLookupService> _logger;
        
        // Cache: subscriptionId -> (resourceId -> (dailyCost, monthlyCost))
        private readonly ConcurrentDictionary<string, Dictionary<string, ResourceCostData>> _costCache = new();
        private readonly ConcurrentDictionary<string, DateTime> _cacheTimestamps = new();
        private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
        
        private const int CacheExpirationMinutes = 30;

        public ResourceCostLookupService(
            ICostManagementClient costManagementClient,
            ILogger<ResourceCostLookupService> logger)
        {
            _costManagementClient = costManagementClient;
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
        /// <returns>ResourceCostData com custo diário e mensal projetado</returns>
        public async Task<ResourceCostData> GetResourceCostDataAsync(string subscriptionId, string resourceId)
        {
            if (string.IsNullOrEmpty(subscriptionId) || string.IsNullOrEmpty(resourceId))
                return new ResourceCostData(0, 0);

            // Garantir que temos os custos carregados para esta subscription
            await EnsureCostsLoadedAsync(subscriptionId);

            // Lookup pelo resourceId (case insensitive)
            if (_costCache.TryGetValue(subscriptionId.ToLowerInvariant(), out var costs))
            {
                var resourceIdLower = resourceId.ToLowerInvariant();
                if (costs.TryGetValue(resourceIdLower, out var cost))
                {
                    return cost;
                }

                // Tentar match parcial (alguns recursos podem ter IDs ligeiramente diferentes)
                var partialMatch = costs.Keys.FirstOrDefault(k => 
                    k.Contains(resourceIdLower) || resourceIdLower.Contains(k));
                if (partialMatch != null)
                {
                    return costs[partialMatch];
                }
            }

            _logger.LogDebug("💰 Custo não encontrado para {ResourceId}", resourceId);
            return new ResourceCostData(0, 0);
        }

        /// <summary>
        /// Obtém custos de múltiplos recursos de uma vez
        /// </summary>
        public async Task<Dictionary<string, ResourceCostData>> GetResourceCostsAsync(string subscriptionId, IEnumerable<string> resourceIds)
        {
            await EnsureCostsLoadedAsync(subscriptionId);
            
            var results = new Dictionary<string, ResourceCostData>(StringComparer.OrdinalIgnoreCase);
            
            if (_costCache.TryGetValue(subscriptionId.ToLowerInvariant(), out var costs))
            {
                foreach (var resourceId in resourceIds)
                {
                    var resourceIdLower = resourceId.ToLowerInvariant();
                    if (costs.TryGetValue(resourceIdLower, out var cost))
                    {
                        results[resourceId] = cost;
                    }
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

        private async Task EnsureCostsLoadedAsync(string subscriptionId)
        {
            var subscriptionIdLower = subscriptionId.ToLowerInvariant();

            // Verificar se já está em cache e não expirou
            if (_cacheTimestamps.TryGetValue(subscriptionIdLower, out var timestamp))
            {
                if (DateTime.UtcNow - timestamp < TimeSpan.FromMinutes(CacheExpirationMinutes))
                {
                    return; // Cache ainda válido
                }
            }

            await _loadSemaphore.WaitAsync();
            try
            {
                // Double-check após obter o lock
                if (_cacheTimestamps.TryGetValue(subscriptionIdLower, out timestamp))
                {
                    if (DateTime.UtcNow - timestamp < TimeSpan.FromMinutes(CacheExpirationMinutes))
                    {
                        return;
                    }
                }

                _logger.LogInformation("💰 Carregando custos do Cost Management para subscription {SubscriptionId}", subscriptionId);

                // Buscar dados dos últimos 30 dias COM granularidade diária para calcular média real
                var endDate = DateTime.UtcNow.Date;
                var startDate = endDate.AddDays(-30);

                var response = await _costManagementClient.QueryCostByResourceAsync(
                    subscriptionId,
                    startDate,
                    endDate,
                    "Daily", // ⚡ Granularidade DIÁRIA para calcular média correta
                    null,    // Sem filtro de serviço
                    CancellationToken.None);

                // Agrupar por recurso e calcular média diária REAL (baseada nos dias com dados)
                var costsByResource = new Dictionary<string, ResourceCostData>(StringComparer.OrdinalIgnoreCase);
                var dayCountByResource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var totalCostByResource = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                
                // Primeiro: agrupar custos por recurso (sem filtrar por valor ainda)
                foreach (var row in response.Rows)
                {
                    if (string.IsNullOrEmpty(row.ResourceId))
                        continue;

                    var resourceIdLower = row.ResourceId.ToLowerInvariant();
                    
                    // Acumular custo total
                    if (!totalCostByResource.ContainsKey(resourceIdLower))
                    {
                        totalCostByResource[resourceIdLower] = 0;
                        dayCountByResource[resourceIdLower] = 0;
                    }
                    
                    // Só conta dias com custo significativo (> R$ 0.10)
                    // Isso evita que dias com custo mínimo inflem a contagem
                    if (row.TotalCost > 0.10m)
                    {
                        totalCostByResource[resourceIdLower] += row.TotalCost;
                        dayCountByResource[resourceIdLower]++;
                    }
                }

                // Calcular custo diário médio e projeção mensal
                foreach (var kvp in totalCostByResource)
                {
                    var resourceId = kvp.Key;
                    var totalCost = kvp.Value;
                    var daysWithData = dayCountByResource[resourceId];
                    
                    // Se não tem dias com custo significativo, pular
                    if (daysWithData == 0 || totalCost <= 0)
                        continue;
                    
                    // Média diária real = total / dias com dados significativos
                    var dailyCost = Math.Round(totalCost / daysWithData, 2);
                    
                    // Projeção mensal = média diária * 30
                    var monthlyCost = Math.Round(dailyCost * 30, 2);
                    
                    costsByResource[resourceId] = new ResourceCostData(dailyCost, monthlyCost);
                    
                    _logger.LogInformation("💰 {ResourceName}: Total R$ {Total:N2} em {Days} dias = R$ {Daily:N2}/dia → ~R$ {Monthly:N2}/mês",
                        resourceId.Split('/').LastOrDefault(), totalCost, daysWithData, dailyCost, monthlyCost);
                }

                _costCache[subscriptionIdLower] = costsByResource;
                _cacheTimestamps[subscriptionIdLower] = DateTime.UtcNow;

                _logger.LogInformation("💰 Carregados custos de {Count} recursos para subscription {SubscriptionId}. Total mensal projetado: R$ {Total:N2}", 
                    costsByResource.Count, subscriptionId, costsByResource.Values.Sum(c => c.MonthlyCost));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "💰 Falha ao carregar custos do Cost Management para {SubscriptionId}. Usando fallback.", subscriptionId);
                // Em caso de falha, colocar cache vazio para não ficar tentando repetidamente
                _costCache[subscriptionIdLower] = new Dictionary<string, ResourceCostData>();
                _cacheTimestamps[subscriptionIdLower] = DateTime.UtcNow;
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }
    }
}
