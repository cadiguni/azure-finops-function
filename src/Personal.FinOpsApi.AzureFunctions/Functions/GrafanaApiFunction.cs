using System.Text.Json;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// 📊 API otimizada para Grafana - dados já agregados e tabulares
/// </summary>
public class GrafanaApiFunction
{
    private readonly GrafanaDataService _grafanaService;
    private readonly ILogger<GrafanaApiFunction> _logger;

    public GrafanaApiFunction(
        GrafanaDataService grafanaService,
        ILogger<GrafanaApiFunction> logger)
    {
        _grafanaService = grafanaService;
        _logger = logger;
    }

    /// <summary>
    /// 📈 Dados agregados por tipo de recurso para Grafana
    /// GET /api/grafana/savings-by-type?date=2024-02-17&subscription=all
    /// </summary>
    [Function("GrafanaSavingsByType")]
    public async Task<HttpResponseData> GetSavingsByTypeAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
    {
        _logger.LogInformation("📊 Grafana API: Savings by Type solicitado");

        try
        {
            var date = req.Query?["date"] ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
            var subscriptionFilter = req.Query?["subscription"] ?? "all";
            
            _logger.LogInformation("📅 Processando: data={date}, subscription={subscription}", date, subscriptionFilter);

            var aggregatedData = await _grafanaService.GetSavingsByResourceTypeAsync(date, subscriptionFilter);

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(aggregatedData);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro na API Grafana Savings by Type");
            
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Erro: {ex.Message}");
            return errorResponse;
        }
    }

    /// <summary>
    /// 🏢 Dados agregados por subscription para Grafana
    /// GET /api/grafana/savings-by-subscription?date=2024-02-17
    /// </summary>
    [Function("GrafanaSavingsBySubscription")]
    public async Task<HttpResponseData> GetSavingsBySubscriptionAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
    {
        _logger.LogInformation("📊 Grafana API: Savings by Subscription solicitado");

        try
        {
            var date = req.Query?["date"] ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
            
            var aggregatedData = await _grafanaService.GetSavingsBySubscriptionAsync(date);

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(aggregatedData);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro na API Grafana Savings by Subscription");
            
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Erro: {ex.Message}");
            return errorResponse;
        }
    }

    /// <summary>
    /// 🎯 Dados detalhados por recurso individual para Grafana
    /// GET /api/grafana/resource-details?date=2024-02-17&subscription=xxx&resourceType=AppServicePlan
    /// </summary>
    [Function("GrafanaResourceDetails")]
    public async Task<HttpResponseData> GetResourceDetailsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
    {
        _logger.LogInformation("📊 Grafana API: Resource Details solicitado");

        try
        {
            var date = req.Query?["date"] ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
            var subscriptionFilter = req.Query?["subscription"] ?? "all";
            var resourceTypeFilter = req.Query?["resourceType"] ?? "all";
            
            var detailedData = await _grafanaService.GetResourceDetailsAsync(date, subscriptionFilter, resourceTypeFilter);

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(detailedData);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro na API Grafana Resource Details");
            
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Erro: {ex.Message}");
            return errorResponse;
        }
    }

    /// <summary>
    /// 📊 Endpoint de teste para verificar se a API está funcionando
    /// GET /api/grafana/health
    /// </summary>
    [Function("GrafanaHealthCheck")]
    public async Task<HttpResponseData> HealthCheckAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "grafana/health")] HttpRequestData req)
    {
        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new 
        { 
            status = "healthy", 
            timestamp = DateTime.UtcNow,
            message = "Grafana API funcionando! 🎉",
            endpoints = new[]
            {
                "GET /api/grafana/savings-by-type?date=2024-02-17&subscription=all",
                "GET /api/grafana/savings-by-subscription?date=2024-02-17", 
                "GET /api/grafana/resource-details?date=2024-02-17&subscription=xxx&resourceType=AppServicePlan"
            }
        });
        return response;
    }

    /// <summary>
    /// 🐛 Debug - Lista blobs disponíveis para uma data
    /// GET /api/grafana/debug?date=2024-02-17
    /// </summary>
    [Function("GrafanaDebug")]
    public async Task<HttpResponseData> DebugBlobsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "grafana/debug")] HttpRequestData req)
    {
        _logger.LogInformation("🐛 Debug: Investigando blobs disponíveis");

        try
        {
            var date = req.Query?["date"] ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
            var analysisDate = DateTime.ParseExact(date, "yyyy-MM-dd", null);

            _logger.LogInformation("🔍 Procurando dados para: {date}", date);

            // Chamar o serviço para debug
            var debugInfo = await _grafanaService.DebugBlobsForDateAsync(analysisDate);

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(debugInfo);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro no debug");
            
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Erro: {ex.Message}");
            return errorResponse;
        }
    }

    /// <summary>
    /// 🐛 Debug específico - Testa leitura direta de um blob
    /// GET /api/grafana/debug-blob?subscription=xxx
    /// </summary>
    [Function("GrafanaDebugBlob")]
    public async Task<HttpResponseData> DebugSpecificBlobAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "grafana/debug-blob")] HttpRequestData req)
    {
        _logger.LogInformation("🐛 Debug: Testando leitura específica de blob");

        try
        {
            var date = req.Query?["date"] ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
            var subscription = req.Query?["subscription"];
            if (string.IsNullOrWhiteSpace(subscription))
            {
                var badRequest = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                await badRequest.WriteStringAsync("Query parameter 'subscription' is required.");
                return badRequest;
            }
            
            var analysisDate = DateTime.ParseExact(date, "yyyy-MM-dd", null);

            var debugInfo = await _grafanaService.DebugSpecificBlobAsync(analysisDate, subscription);

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(debugInfo);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro no debug específico");
            
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Erro: {ex.Message}");
            return errorResponse;
        }
    }
}
