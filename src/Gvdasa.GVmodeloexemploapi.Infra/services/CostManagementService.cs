using Gvdasa.GVmodeloexemploapi.Modelos.FinOps;

namespace Gvdasa.GVmodeloexemploapi.Infra.Services.FinOps;

public interface ICostManagementService
{
    Task<IEnumerable<CostRecord>> GetCostsBySubscriptionAsync(string subscriptionId, DateTime startDate, DateTime endDate);
    Task<IEnumerable<CostRecord>> GetCostsByResourceGroupAsync(string subscriptionId, string resourceGroupName, DateTime startDate, DateTime endDate);
    Task<IEnumerable<CostRecord>> GetCostsByResourceTypeAsync(string subscriptionId, string resourceType, DateTime startDate, DateTime endDate);
    Task<IEnumerable<CostRecord>> GetCostsForAllSubscriptionsAsync(DateTime startDate, DateTime endDate, string[]? subscriptionFilter = null);
}

public class CostManagementService : ICostManagementService
{
    private readonly ILogger<CostManagementService> _logger;
    private readonly HttpClient _httpClient;

    public CostManagementService(ILogger<CostManagementService> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<IEnumerable<CostRecord>> GetCostsBySubscriptionAsync(string subscriptionId, DateTime startDate, DateTime endDate)
    {
        try
        {
            _logger.LogInformation("Buscando custos para subscription {SubscriptionId} de {StartDate} até {EndDate}", 
                subscriptionId, startDate, endDate);
            
            // Implementar chamada para Azure Cost Management API
            // Scope: /subscriptions/{subscriptionId}
            var scope = $"/subscriptions/{subscriptionId}";
            return await QueryCostManagementApi(scope, startDate, endDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar custos para subscription {SubscriptionId}", subscriptionId);
            throw;
        }
    }

    public async Task<IEnumerable<CostRecord>> GetCostsByResourceGroupAsync(string subscriptionId, string resourceGroupName, DateTime startDate, DateTime endDate)
    {
        try
        {
            var scope = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}";
            return await QueryCostManagementApi(scope, startDate, endDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar custos para resource group {ResourceGroup}", resourceGroupName);
            throw;
        }
    }

    public async Task<IEnumerable<CostRecord>> GetCostsByResourceTypeAsync(string subscriptionId, string resourceType, DateTime startDate, DateTime endDate)
    {
        try
        {
            var scope = $"/subscriptions/{subscriptionId}";
            var costs = await QueryCostManagementApi(scope, startDate, endDate);
            return costs.Where(c => c.ResourceType.Equals(resourceType, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar custos por tipo de recurso {ResourceType}", resourceType);
            throw;
        }
    }

    public async Task<IEnumerable<CostRecord>> GetCostsForAllSubscriptionsAsync(DateTime startDate, DateTime endDate, string[]? subscriptionFilter = null)
    {
        try
        {
            // Para múltiplas assinaturas, usar Management Group scope
            // Scope: /providers/Microsoft.Management/managementGroups/{managementGroupId}
            var scope = "/providers/Microsoft.Management/managementGroups/root"; // Ajustar conforme necessário
            var allCosts = await QueryCostManagementApi(scope, startDate, endDate);
            
            if (subscriptionFilter?.Any() == true)
            {
                allCosts = allCosts.Where(c => subscriptionFilter.Contains(c.SubscriptionId));
            }
            
            return allCosts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar custos para todas as assinaturas");
            throw;
        }
    }

    private async Task<IEnumerable<CostRecord>> QueryCostManagementApi(string scope, DateTime startDate, DateTime endDate)
    {
        // Implementar a query real para Azure Cost Management API
        // Por enquanto, mock para estrutura
        _logger.LogInformation("Executando query Cost Management para scope {Scope}", scope);
        
        // TODO: Implementar chamada real para API
        // https://management.azure.com/{scope}/providers/Microsoft.CostManagement/query?api-version=2021-10-01
        
        await Task.Delay(100); // Simular latência
        
        return new List<CostRecord>
        {
            new CostRecord
            {
                SubscriptionId = ExtractSubscriptionFromScope(scope),
                ResourceId = "/subscriptions/example/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1",
                ResourceName = "vm1",
                ResourceType = "Microsoft.Compute/virtualMachines",
                ResourceGroupName = "rg1",
                MonthlyCost = 800.50m,
                DailyCost = 26.68m,
                AnalysisDate = DateTime.UtcNow,
                CostPeriodStart = startDate,
                CostPeriodEnd = endDate
            }
        };
    }

    private string ExtractSubscriptionFromScope(string scope)
    {
        // Extrair subscription ID do scope
        var parts = scope.Split('/');
        var subscriptionIndex = Array.IndexOf(parts, "subscriptions");
        return subscriptionIndex >= 0 && subscriptionIndex + 1 < parts.Length 
            ? parts[subscriptionIndex + 1] 
            : "unknown";
    }
}