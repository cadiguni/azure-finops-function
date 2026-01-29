using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Web;

namespace Gvdasa.FinOpsApi.AzureFunctions;

/// <summary>
/// 🧪 NÍVEL 2 — Azure Function LOCAL para coleta de dados de custo
/// Roda localmente sem depender do Azure (por enquanto)
/// </summary>
public class CostManagementCollector
{
    private readonly ILogger<CostManagementCollector> _logger;

    public CostManagementCollector(ILogger<CostManagementCollector> logger)
    {
        _logger = logger;
    }

    [Function("CostManagementCollector")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "cost-management/{action?}")] HttpRequestData req,
        string? action,
        FunctionContext executionContext)
    {
        _logger.LogInformation("🚀 FinOps Function executada - Ação: {Action}", action ?? "default");

        try
        {
            // Parsear parâmetros da query string
            var query = HttpUtility.ParseQueryString(req.Url.Query);
            var subscriptionId = query["subscriptionId"];
            var managementGroupId = query["managementGroupId"] ?? Environment.GetEnvironmentVariable("MANAGEMENT_GROUP_ID");
            var days = int.TryParse(query["days"], out var d) ? d : 7;

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");

            var result = action?.ToLower() switch
            {
                "collect" => await SimulateCollectCostData(subscriptionId, managementGroupId, days),
                "analyze" => await SimulateAnalyzeCosts(subscriptionId, days),
                "optimize" => await SimulateOptimizationRecommendations(subscriptionId),
                _ => await GetFunctionStatus()
            };

            await response.WriteStringAsync(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro na execução da Function");
            
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new
            {
                error = "Erro interno da função",
                message = ex.Message,
                timestamp = DateTime.UtcNow
            }));
            return errorResponse;
        }
    }

    private async Task<object> SimulateCollectCostData(string? subscriptionId, string? managementGroupId, int days)
    {
        _logger.LogInformation("📊 Simulando coleta de dados de custo...");
        
        await Task.Delay(100); // Simular processamento
        
        if (string.IsNullOrEmpty(subscriptionId) && string.IsNullOrEmpty(managementGroupId))
        {
            throw new ArgumentException("subscriptionId ou managementGroupId deve ser informado");
        }

        return new
        {
            action = "collect",
            scope = subscriptionId ?? $"MG:{managementGroupId}",
            period = $"Últimos {days} dias",
            status = "✅ Simulação executada com sucesso",
            data = new
            {
                totalCost = 1250.75m,
                currency = "BRL",
                resourceCount = 45,
                topResources = new[]
                {
                    new { name = "vm-prod-01", cost = 156.30m, type = "Microsoft.Compute/virtualMachines" },
                    new { name = "disk-premium-01", cost = 89.50m, type = "Microsoft.Compute/disks" },
                    new { name = "app-service-prod", cost = 125.00m, type = "Microsoft.Web/sites" }
                }
            },
            timestamp = DateTime.UtcNow
        };
    }

    private async Task<object> SimulateAnalyzeCosts(string? subscriptionId, int days)
    {
        _logger.LogInformation("🔍 Simulando análise de custos...");
        
        await Task.Delay(200); // Simular processamento mais demorado
        
        return new
        {
            action = "analyze",
            subscription = subscriptionId ?? "N/A",
            analysis = new
            {
                period = $"Últimos {days} dias",
                trends = new
                {
                    direction = "📈 Crescimento",
                    percentage = 15.5,
                    mainDrivers = new[] { "Compute", "Storage" }
                },
                categories = new[]
                {
                    new { category = "Compute", cost = 750.25m, percentage = 60 },
                    new { category = "Storage", cost = 300.50m, percentage = 24 },
                    new { category = "Networking", cost = 200.00m, percentage = 16 }
                }
            },
            timestamp = DateTime.UtcNow
        };
    }

    private async Task<object> SimulateOptimizationRecommendations(string? subscriptionId)
    {
        _logger.LogInformation("⚡ Simulando recomendações de otimização...");
        
        await Task.Delay(150);
        
        return new
        {
            action = "optimize",
            subscription = subscriptionId ?? "N/A",
            recommendations = new[]
            {
                new 
                {
                    type = "VM Idle",
                    resource = "vm-dev-test-01",
                    issue = "VM com baixo uso de CPU (<5%)",
                    recommendation = "Redimensionar ou desligar",
                    potentialSaving = 450.00m,
                    priority = "High"
                },
                new 
                {
                    type = "Unattached Disk",
                    resource = "disk-orphaned-02",
                    issue = "Disco não anexado há mais de 30 dias",
                    recommendation = "Remover disco não utilizado",
                    potentialSaving = 125.50m,
                    priority = "Medium"
                },
                new 
                {
                    type = "App Service Plan",
                    resource = "plan-basic-01",
                    issue = "Plano com baixa utilização",
                    recommendation = "Downgrade para plano menor",
                    potentialSaving = 200.00m,
                    priority = "Medium"
                }
            },
            totalPotentialSaving = 775.50m,
            timestamp = DateTime.UtcNow
        };
    }

    private async Task<object> GetFunctionStatus()
    {
        await Task.Delay(50);
        
        return new
        {
            service = "FinOps Cost Management Function",
            version = "1.0.0",
            status = "🟢 Online",
            environment = Environment.GetEnvironmentVariable("ambiente") ?? "local",
            availableActions = new[]
            {
                "GET /api/cost-management - Status da função",
                "GET /api/cost-management/collect?subscriptionId=xxx - Coletar dados de custo",
                "GET /api/cost-management/analyze?subscriptionId=xxx&days=30 - Analisar tendências",
                "GET /api/cost-management/optimize?subscriptionId=xxx - Recomendações de otimização"
            },
            timestamp = DateTime.UtcNow
        };
    }
}