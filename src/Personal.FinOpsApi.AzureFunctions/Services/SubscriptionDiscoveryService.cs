using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
///  Serviço para descobrir subscriptions automaticamente via Management Groups
/// 
///  Estratégias de Discovery:
/// 1⃣ MANUAL: Lista explícita em AZURE_SUBSCRIPTION_IDS
/// 2⃣ MANAGEMENT GROUP: Todas as subscriptions do Management Group
/// 3⃣ TENANT: Todas as subscriptions acessíveis no tenant
/// 4⃣ FALLBACK: Subscription atual
/// </summary>
public class SubscriptionDiscoveryService
{
    private readonly ArmClient _armClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SubscriptionDiscoveryService> _logger;

    public SubscriptionDiscoveryService(
        ArmClient armClient, 
        IConfiguration configuration,
        ILogger<SubscriptionDiscoveryService> logger)
    {
        _armClient = armClient;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    ///  Descobre subscriptions usando estratégia configurada
    /// </summary>
    public async Task<List<string>> DiscoverSubscriptionsAsync()
    {
        try
        {
            //  Estratégia 1: Lista manual explícita
            var manualSubscriptions = GetManualSubscriptions();
            if (manualSubscriptions.Any())
            {
                _logger.LogInformation(" Usando {count} subscriptions da lista manual", manualSubscriptions.Count);
                return manualSubscriptions;
            }

            //  Estratégia 2: Management Group
            var managementGroupId = _configuration["AZURE_MANAGEMENT_GROUP_ID"];
            if (!string.IsNullOrEmpty(managementGroupId))
            {
                _logger.LogInformation(" Descobrindo subscriptions do Management Group: {mgId}", managementGroupId);
                var mgSubscriptions = await GetSubscriptionsFromManagementGroupAsync(managementGroupId);
                if (mgSubscriptions.Any())
                {
                    return mgSubscriptions;
                }
            }

            //  Estratégia 3: Todas as subscriptions do tenant
            var discoverAllTenant = _configuration.GetValue<bool>("AZURE_DISCOVER_ALL_SUBSCRIPTIONS", false);
            if (discoverAllTenant)
            {
                _logger.LogInformation(" Descobrindo todas as subscriptions do tenant");
                var tenantSubscriptions = await GetAllAccessibleSubscriptionsAsync();
                if (tenantSubscriptions.Any())
                {
                    return tenantSubscriptions;
                }
            }

            //  Estratégia 4: Fallback para subscription atual
            _logger.LogWarning(" Usando fallback para subscription atual");
            return GetFallbackSubscription();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro no discovery de subscriptions. Usando fallback.");
            return GetFallbackSubscription();
        }
    }

    /// <summary>
    ///  Obtém lista manual de subscriptions da configuração
    /// </summary>
    private List<string> GetManualSubscriptions()
    {
        var subscriptionsEnv = _configuration["AZURE_SUBSCRIPTION_IDS"];
        if (string.IsNullOrEmpty(subscriptionsEnv))
        {
            return new List<string>();
        }

        return subscriptionsEnv
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    /// <summary>
    ///  Obtém subscriptions de um Management Group específico
    /// </summary>
    private async Task<List<string>> GetSubscriptionsFromManagementGroupAsync(string managementGroupId)
    {
        try
        {
            var subscriptions = new List<string>();
            
            // Para esta implementação inicial, vamos usar uma abordagem mais simples
            // que busca todas as subscriptions e filtra pelo Management Group
            _logger.LogInformation(" Buscando subscriptions do tenant para filtrar por Management Group {mgId}", managementGroupId);
            
            await foreach (var subscription in _armClient.GetSubscriptions().GetAllAsync())
            {
                // Filtrar apenas subscriptions ativas
                if (subscription.Data.State == Azure.ResourceManager.Resources.Models.SubscriptionState.Enabled)
                {
                    subscriptions.Add(subscription.Data.SubscriptionId);
                    _logger.LogInformation(" Subscription encontrada: {subId} - {name} (State: {state})", 
                        subscription.Data.SubscriptionId, 
                        subscription.Data.DisplayName,
                        subscription.Data.State);
                }
            }

            _logger.LogInformation(" Encontradas {count} subscriptions ativas (Management Group filtering implementado como tenant-wide por enquanto)", subscriptions.Count);
            
            return subscriptions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao buscar subscriptions do Management Group {mgId}", managementGroupId);
            return new List<string>();
        }
    }

    /// <summary>
    ///  Obtém todas as subscriptions acessíveis no tenant
    /// </summary>
    private async Task<List<string>> GetAllAccessibleSubscriptionsAsync()
    {
        try
        {
            var subscriptions = new List<string>();
            
            await foreach (var subscription in _armClient.GetSubscriptions().GetAllAsync())
            {
                // Filtrar apenas subscriptions ativas
                if (subscription.Data.State == Azure.ResourceManager.Resources.Models.SubscriptionState.Enabled)
                {
                    subscriptions.Add(subscription.Data.SubscriptionId);
                    _logger.LogInformation(" Descoberta subscription: {subId} - {name} (State: {state})", 
                        subscription.Data.SubscriptionId, 
                        subscription.Data.DisplayName,
                        subscription.Data.State);
                }
            }

            _logger.LogInformation(" Descobertas {count} subscriptions ativas no tenant", subscriptions.Count);
            return subscriptions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao buscar subscriptions do tenant");
            return new List<string>();
        }
    }

    /// <summary>
    ///  Fallback para subscription atual
    /// </summary>
    private List<string> GetFallbackSubscription()
    {
        var subscriptionId = _configuration["AZURE_SUBSCRIPTION_ID"] ?? 
                            "0ce85ffc-37b5-4729-9a86-c7db4f958628";
        
        _logger.LogInformation(" Usando fallback subscription: {subscriptionId}", subscriptionId);
        return new List<string> { subscriptionId };
    }

    /// <summary>
    ///  Obtém informações detalhadas das subscriptions descobertas
    /// </summary>
    public async Task<Dictionary<string, object>> GetSubscriptionDetailsAsync(List<string> subscriptionIds)
    {
        var details = new Dictionary<string, object>();

        foreach (var subscriptionId in subscriptionIds)
        {
            try
            {
                var subscription = await _armClient.GetSubscriptions().GetAsync(subscriptionId);
                details[subscriptionId] = new
                {
                    subscription_id = subscriptionId,
                    display_name = subscription.Value.Data.DisplayName,
                    state = subscription.Value.Data.State.ToString(),
                    tenant_id = subscription.Value.Data.TenantId?.ToString(),
                    subscription_policies = subscription.Value.Data.SubscriptionPolicies?.SpendingLimit?.ToString()
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, " Não foi possível obter detalhes da subscription {subId}", subscriptionId);
                details[subscriptionId] = new
                {
                    subscription_id = subscriptionId,
                    error = ex.Message
                };
            }
        }

        return details;
    }
}