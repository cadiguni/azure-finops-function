using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Services;
using System.Net;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// 📊 FINOPS DATA READER - Function HTTP para consumir dados das análises
/// 
/// 🎯 ENDPOINTS:
/// GET /api/finops/data?date=2026-02-12&type=summary
/// GET /api/finops/data?date=2026-02-12&subscription=xxx&type=raw
/// GET /api/finops/data?date=2026-02-12&type=subscriptions
/// </summary>
public class FinOpsDataReaderFunction
{
    private readonly ILogger<FinOpsDataReaderFunction> _logger;
    private readonly AnalysisStorageService _storageService;

    public FinOpsDataReaderFunction(ILogger<FinOpsDataReaderFunction> logger, AnalysisStorageService storageService)
    {
        _logger = logger;
        _storageService = storageService;
    }

    [Function("GetFinOpsData")]
    public async Task<HttpResponseData> GetFinOpsData(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "finops/data")] HttpRequestData req)
    {
        try
        {
            // Parâmetros da query
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var dateParam = query["date"];
            var subscriptionParam = query["subscription"];
            var typeParam = query["type"] ?? "summary"; // summary, raw, subscriptions

            if (!DateTime.TryParse(dateParam, out var date))
            {
                date = DateTime.Today.AddDays(-1); // Ontem por padrão
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");

            switch (typeParam.ToLower())
            {
                case "subscriptions":
                    // Lista subscriptions de uma data
                    var subscriptions = await _storageService.ListSubscriptionsByDateAsync(date);
                    var subsJson = JsonSerializer.Serialize(new { Date = date.ToString("yyyy-MM-dd"), Subscriptions = subscriptions });
                    await response.WriteStringAsync(subsJson);
                    break;

                case "raw":
                    // Dados brutos de uma subscription específica
                    if (string.IsNullOrEmpty(subscriptionParam))
                    {
                        response = req.CreateResponse(HttpStatusCode.BadRequest);
                        await response.WriteStringAsync("{\"error\":\"subscription parameter required for raw data\"}");
                        return response;
                    }
                    
                    var rawData = await _storageService.GetAnalysisAsync(date, subscriptionParam);
                    var rawJson = JsonSerializer.Serialize(rawData);
                    await response.WriteStringAsync(rawJson);
                    break;

                case "summary":
                default:
                    // Summary de todas as subscriptions do dia
                    var allData = await _storageService.GetDailyAnalysisAsync(date);
                    var summary = new
                    {
                        Date = date.ToString("yyyy-MM-dd"),
                        TotalRecommendations = allData.Count,
                        TotalSavings = allData.Sum(r => r.EstimatedMonthlySavings),
                        ByType = allData.GroupBy(r => r.Type).Select(g => new 
                        { 
                            Type = g.Key, 
                            Count = g.Count(), 
                            Savings = g.Sum(r => r.EstimatedMonthlySavings) 
                        }),
                        BySubscription = allData.GroupBy(r => r.SubscriptionId).Select(g => new 
                        { 
                            SubscriptionId = g.Key, 
                            Count = g.Count(), 
                            Savings = g.Sum(r => r.EstimatedMonthlySavings) 
                        })
                    };
                    var summaryJson = JsonSerializer.Serialize(summary);
                    await response.WriteStringAsync(summaryJson);
                    break;
            }

            _logger.LogInformation("✅ Dados FinOps retornados: tipo={type}, date={date}", typeParam, date);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao obter dados FinOps");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"{{\"error\":\"{ex.Message}\"}}");
            return errorResponse;
        }
    }
}