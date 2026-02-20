using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Azure.Core;

namespace Personal.FinOpsApi.AzureFunctions.Analyzers
{
    public class DuplicateResourceAnalyzer
    {
        private readonly ILogger<DuplicateResourceAnalyzer> _logger;
        private readonly ArmClient _armClient;
        
        public DuplicateResourceAnalyzer(ILogger<DuplicateResourceAnalyzer> logger, DefaultAzureCredential credential)
        {
            _logger = logger;
            _armClient = new ArmClient(credential);
        }

        /// <summary>
        /// 🔍 ANÁLISE DE DUPLICATAS: Detecta recursos com nomes idênticos DENTRO DA MESMA SUBSCRIPTION
        /// 
        /// ⚠️ IMPORTANTE: Recursos com mesmo nome em subscriptions DIFERENTES são considerados VÁLIDOS
        /// 🎯 ESCOPO: Apenas intra-subscription (recursos duplicados na mesma subscription)
        /// 
        /// Exemplos:
        /// ✅ VÁLIDO: vm-web01 em Sub-A e vm-web01 em Sub-B (subscriptions diferentes)
        /// ❌ DUPLICATA: vm-web01 e vm-web01 na mesma Sub-A (mesma subscription)
        /// </summary>
        public async Task<List<DuplicateResourceGroup>> AnalyzeDuplicatesAcrossSubscriptionsAsync(
            List<string> subscriptionIds)
        {
            _logger.LogInformation("🔍 Iniciando análise de recursos duplicados em {Count} assinaturas", 
                subscriptionIds.Count);

            var allResources = new List<ResourceInfo>();
            var duplicateGroups = new List<DuplicateResourceGroup>();

            // Coletar recursos de todas as assinaturas
            foreach (var subscriptionId in subscriptionIds)
            {
                try
                {
                    var resources = await CollectResourcesFromSubscriptionAsync(subscriptionId);
                    allResources.AddRange(resources);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Erro ao coletar recursos da assinatura {SubscriptionId}", subscriptionId);
                }
            }

            // 🎯 CORREÇÃO: Agrupar APENAS dentro da mesma subscription (recursos com mesmo nome em subscriptions diferentes SÃO VÁLIDOS)
            var resourceGroups = allResources
                .GroupBy(r => new { r.Name, r.Type, r.SubscriptionId }) // ✅ INCLUIR SubscriptionId no agrupamento
                .Where(g => g.Count() > 1) // Apenas grupos com 2+ recursos NA MESMA subscription
                .ToList();

            _logger.LogInformation("🔍 Agrupamento por Nome+Tipo+Subscription: {GroupCount} grupos encontrados", resourceGroups.Count);

            foreach (var group in resourceGroups)
            {
                // 🔍 VALIDAÇÃO: Confirmar que todos os recursos estão na mesma subscription
                var subscriptions = group.Select(r => r.SubscriptionId).Distinct().ToList();
                if (subscriptions.Count > 1)
                {
                    _logger.LogWarning("⚠️ INCONSISTÊNCIA: Grupo {Name}:{Type} span múltiplas subscriptions: {Subs}", 
                        group.Key.Name, group.Key.Type, string.Join(", ", subscriptions));
                    continue; // Pular este grupo inconsistente
                }

                var duplicateGroup = new DuplicateResourceGroup
                {
                    Name = group.Key.Name,
                    ResourceType = group.Key.Type,
                    Count = group.Count(),
                    Resources = group.ToList(),
                    SimilarityScore = 1.0, // 100% match para nome + tipo idênticos NA MESMA subscription
                    PotentialSavings = await EstimatePotentialSavingsAsync(group.ToList())
                };

                duplicateGroups.Add(duplicateGroup);
                
                _logger.LogInformation("📦 Duplicatas encontradas: {Name} ({Type}) - {Count} recursos na subscription {Sub}", 
                    group.Key.Name, group.Key.Type, group.Count(), subscriptions[0]);
            }

            _logger.LogInformation("✅ Encontrados {Count} grupos de recursos duplicados", duplicateGroups.Count);
            
            return duplicateGroups.OrderByDescending(g => g.PotentialSavings).ToList();
        }

        private async Task<List<ResourceInfo>> CollectResourcesFromSubscriptionAsync(string subscriptionId)
        {
            var resources = new List<ResourceInfo>();
            
            try
            {
                var subscription = _armClient.GetSubscriptionResource(
                    SubscriptionResource.CreateResourceIdentifier(subscriptionId));

                // Usar Azure Resource Graph seria mais eficiente, mas vamos usar ARM direto
                await foreach (var resourceGroup in subscription.GetResourceGroups())
                {
                    foreach (var resource in resourceGroup.GetGenericResources())
                    {
                        resources.Add(new ResourceInfo
                        {
                            Id = resource.Id,
                            Name = resource.Data.Name,
                            Type = resource.Data.ResourceType,
                            Location = resource.Data.Location.Name ?? "unknown",
                            ResourceGroupName = resourceGroup.Data.Name,
                            SubscriptionId = subscriptionId,
                            Tags = resource.Data.Tags?.ToDictionary(t => t.Key, t => t.Value) ?? new Dictionary<string, string>()
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro ao acessar assinatura {SubscriptionId}", subscriptionId);
            }

            _logger.LogInformation("📊 Coletados {Count} recursos da assinatura {SubscriptionId}", 
                resources.Count, subscriptionId);
            
            return resources;
        }

        private async Task<decimal> EstimatePotentialSavingsAsync(List<ResourceInfo> duplicateResources)
        {
            // Estimativa simplificada baseada no tipo de recurso
            decimal estimatedSavings = 0;

            var typeSavings = new Dictionary<string, decimal>
            {
                ["Microsoft.Compute/virtualMachines"] = 150m,           // $150/mês por VM
                ["Microsoft.Storage/storageAccounts"] = 25m,            // $25/mês por Storage Account
                ["Microsoft.Network/publicIPAddresses"] = 5m,           // $5/mês por IP público
                ["Microsoft.Web/sites"] = 50m,                         // $50/mês por App Service
                ["Microsoft.Sql/servers/databases"] = 100m,            // $100/mês por SQL Database
                ["Microsoft.Network/loadBalancers"] = 30m,             // $30/mês por Load Balancer
            };

            foreach (var resource in duplicateResources.Skip(1)) // Excluir o primeiro (manter um)
            {
                if (typeSavings.TryGetValue(resource.Type.ToString(), out var monthlySavings))
                {
                    estimatedSavings += monthlySavings;
                }
                else
                {
                    estimatedSavings += 10m; // Valor padrão para recursos não categorizados
                }
            }

            return estimatedSavings;
        }
    }

    public class ResourceInfo
    {
        public ResourceIdentifier Id { get; set; } = default!;
        public string Name { get; set; } = string.Empty;
        public ResourceType Type { get; set; }
        public string Location { get; set; } = string.Empty;
        public string ResourceGroupName { get; set; } = string.Empty;
        public string SubscriptionId { get; set; } = string.Empty;
        public Dictionary<string, string> Tags { get; set; } = new();
    }

    public class DuplicateResourceGroup
    {
        public string Name { get; set; } = string.Empty;
        public ResourceType ResourceType { get; set; }
        public int Count { get; set; }
        public List<ResourceInfo> Resources { get; set; } = new();
        public double SimilarityScore { get; set; }
        public decimal PotentialSavings { get; set; }
        
        public List<string> GetSubscriptions() => Resources.Select(r => r.SubscriptionId).Distinct().ToList();
        public List<string> GetLocations() => Resources.Select(r => r.Location).Distinct().ToList();
    }
}