using Gvdasa.FinOpsApi.Modelos.FinOps;

namespace Gvdasa.FinOpsApi.UnitTests.Fixtures;

/// <summary>
/// Factory para criação de dados fake para testes
/// </summary>
public static class FakeDataFactory
{
    /// <summary>
    /// Cria dados de uso de VM idle (baixo CPU/Memória)
    /// </summary>
    public static List<ResourceUsage> CreateIdleVmUsage()
    {
        return new()
        {
            new ResourceUsage
            {
                Id = Guid.NewGuid(),
                ResourceId = "/subscriptions/sub-123/resourceGroups/rg-test/providers/Microsoft.Compute/virtualMachines/vm-idle-01",
                ResourceName = "vm-idle-01",
                ResourceType = "Microsoft.Compute/virtualMachines",
                SubscriptionId = "sub-123",
                ResourceGroup = "rg-test",
                Location = "East US",
                ManagementGroupName = "VisualStudio", // MPN environment
                MeasurementDate = DateTime.UtcNow.AddDays(-1),
                CpuPercentage = 1.2, // Muito baixo - idle
                MemoryPercentage = 8.5, // Baixo uso
                IsRunning = true,
                DaysInactive = 0,
                Tags = new Dictionary<string, string>
                {
                    ["environment"] = "dev",
                    ["owner"] = "joao.silva@gvdasa.com.br",
                    ["cost-center"] = "TI-Development"
                }
            }
        };
    }

    /// <summary>
    /// Cria dados de uso de VM produção (alta utilização)
    /// </summary>
    public static List<ResourceUsage> CreateProductionVmUsage()
    {
        return new()
        {
            new ResourceUsage
            {
                Id = Guid.NewGuid(),
                ResourceId = "/subscriptions/sub-456/resourceGroups/rg-prod/providers/Microsoft.Compute/virtualMachines/vm-prod-web01",
                ResourceName = "vm-prod-web01",
                ResourceType = "Microsoft.Compute/virtualMachines",
                SubscriptionId = "sub-456",
                ResourceGroup = "rg-prod",
                Location = "Brazil South",
                ManagementGroupName = "Setores", // Produção
                MeasurementDate = DateTime.UtcNow.AddDays(-1),
                CpuPercentage = 65.3, // Alta utilização
                MemoryPercentage = 78.1,
                IsRunning = true,
                DaysInactive = 0,
                Tags = new Dictionary<string, string>
                {
                    ["environment"] = "prod",
                    ["owner"] = "admin@gvdasa.com.br",
                    ["cost-center"] = "Setores-Prod"
                }
            }
        };
    }

    /// <summary>
    /// Cria dados de disco não anexado
    /// </summary>
    public static List<ResourceUsage> CreateUnattachedDiskUsage()
    {
        return new()
        {
            new ResourceUsage
            {
                Id = Guid.NewGuid(),
                ResourceId = "/subscriptions/sub-123/resourceGroups/rg-test/providers/Microsoft.Compute/disks/disk-orphan-01",
                ResourceName = "disk-orphan-01",
                ResourceType = "Microsoft.Compute/disks",
                SubscriptionId = "sub-123",
                ResourceGroup = "rg-test",
                Location = "East US",
                MeasurementDate = DateTime.UtcNow.AddDays(-1),
                IsRunning = false,
                DaysInactive = 15, // 15 dias sem uso
                Tags = new Dictionary<string, string>
                {
                    ["environment"] = "dev"
                    // Missing owner and cost-center tags - governança issue
                }
            }
        };
    }

    /// <summary>
    /// Cria dados de custo alto para VM
    /// </summary>
    public static List<CostRecord> CreateHighCostVm()
    {
        return new()
        {
            new CostRecord
            {
                Id = Guid.NewGuid(),
                ResourceId = "/subscriptions/sub-123/resourceGroups/rg-test/providers/Microsoft.Compute/virtualMachines/vm-idle-01",
                ResourceName = "vm-idle-01",
                SubscriptionId = "sub-123",
                ResourceGroup = "rg-test",
                CostDate = DateTime.UtcNow.AddDays(-1),
                DailyCost = 26.67, // ~800/mês
                Currency = "BRL",
                ServiceName = "Virtual Machines",
                Tags = new Dictionary<string, object>
                {
                    ["environment"] = "dev",
                    ["size"] = "Standard_D4s_v3"
                }
            }
        };
    }

    /// <summary>
    /// Cria dados de App Service com baixo tráfego
    /// </summary>
    public static List<ResourceUsage> CreateLowTrafficAppService()
    {
        return new()
        {
            new ResourceUsage
            {
                Id = Guid.NewGuid(),
                ResourceId = "/subscriptions/sub-123/resourceGroups/rg-test/providers/Microsoft.Web/sites/app-lowtraffic",
                ResourceName = "app-lowtraffic",
                ResourceType = "Microsoft.Web/sites",
                SubscriptionId = "sub-123",
                ResourceGroup = "rg-test",
                Location = "East US",
                MeasurementDate = DateTime.UtcNow.AddDays(-1),
                HttpRequests = 45, // Muito baixo tráfego
                ResponseTime = 120.5,
                IsRunning = true,
                Tags = new Dictionary<string, string>
                {
                    ["environment"] = "dev",
                    ["app-service-plan"] = "P1v2"
                }
            }
        };
    }

    /// <summary>
    /// Cria recursos sem tags obrigatórias (problema de governança)
    /// </summary>
    public static List<ResourceUsage> CreateResourcesWithMissingTags()
    {
        return new()
        {
            new ResourceUsage
            {
                Id = Guid.NewGuid(),
                ResourceId = "/subscriptions/sub-123/resourceGroups/rg-test/providers/Microsoft.Storage/storageAccounts/storphanaccount",
                ResourceName = "storphanaccount",
                ResourceType = "Microsoft.Storage/storageAccounts",
                SubscriptionId = "sub-123",
                ResourceGroup = "rg-test",
                Location = "East US",
                MeasurementDate = DateTime.UtcNow.AddDays(-1),
                IsRunning = true,
                Tags = new Dictionary<string, string>
                {
                    // Todas as tags obrigatórias ausentes!
                    ["created-by"] = "terraform"
                }
            }
        };
    }

    /// <summary>
    /// Cria mix de recursos para teste de orquestração completa
    /// </summary>
    public static List<ResourceUsage> CreateMixedResourcesUsage()
    {
        var resources = new List<ResourceUsage>();
        resources.AddRange(CreateIdleVmUsage());
        resources.AddRange(CreateProductionVmUsage());
        resources.AddRange(CreateUnattachedDiskUsage());
        resources.AddRange(CreateLowTrafficAppService());
        resources.AddRange(CreateResourcesWithMissingTags());
        return resources;
    }

    /// <summary>
    /// Cria dados de custo para mix de recursos
    /// </summary>
    public static List<CostRecord> CreateMixedCostRecords()
    {
        return new()
        {
            new CostRecord
            {
                Id = Guid.NewGuid(),
                ResourceId = "/subscriptions/sub-123/resourceGroups/rg-test/providers/Microsoft.Compute/virtualMachines/vm-idle-01",
                DailyCost = 26.67,
                Currency = "BRL"
            },
            new CostRecord
            {
                Id = Guid.NewGuid(),
                ResourceId = "/subscriptions/sub-456/resourceGroups/rg-prod/providers/Microsoft.Compute/virtualMachines/vm-prod-web01",
                DailyCost = 45.30,
                Currency = "BRL"
            },
            new CostRecord
            {
                Id = Guid.NewGuid(),
                ResourceId = "/subscriptions/sub-123/resourceGroups/rg-test/providers/Microsoft.Compute/disks/disk-orphan-01",
                DailyCost = 8.50,
                Currency = "BRL"
            }
        };
    }
}