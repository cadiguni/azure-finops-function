using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Azure.Core;

namespace Gvdasa.FinOpsApi.AzureFunctions.Analyzers
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

            // Opção A: Agrupar por nome + tipo (mais simples e eficaz)
            var resourceGroups = allResources
                .GroupBy(r => new { r.Name, r.Type })
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in resourceGroups)
            {
                var duplicateGroup = new DuplicateResourceGroup
                {
                    Name = group.Key.Name,
                    ResourceType = group.Key.Type,
                    Count = group.Count(),
                    Resources = group.ToList(),
                    SimilarityScore = 1.0, // 100% match para nome + tipo idênticos
                    PotentialSavings = await EstimatePotentialSavingsAsync(group.ToList())
                };

                duplicateGroups.Add(duplicateGroup);
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