using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// APIs padronizadas para o frontend FinOps Portal.
/// Complementam as APIs existentes com endpoints otimizados para o SPA.
/// </summary>
public class FrontendApiFunction
{
    private readonly AnalysisStorageService _storageService;
    private readonly ILogger<FrontendApiFunction> _logger;

    public FrontendApiFunction(
        AnalysisStorageService storageService,
        ILogger<FrontendApiFunction> logger)
    {
        _storageService = storageService;
        _logger = logger;
    }

    /// <summary>
    /// Retorna recomendações do dia com filtros opcionais.
    /// GET /api/recommendations?date=YYYY-MM-DD&subscriptionId=xxx
    /// </summary>
    [Function("GetRecommendations")]
    public async Task<HttpResponseData> GetRecommendations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "recommendations")] HttpRequestData req)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var dateParam = query["date"];
            var subscriptionId = query["subscriptionId"];

            var date = DateTime.TryParse(dateParam, out var parsed)
                ? parsed.Date
                : DateTime.UtcNow.Date;

            var recommendations = await _storageService.GetDailyAnalysisAsync(date);

            if (!string.IsNullOrEmpty(subscriptionId))
            {
                recommendations = recommendations
                    .Where(r => r.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var summary = new
            {
                date = date.ToString("yyyy-MM-dd"),
                totalRecommendations = recommendations.Count,
                totalEstimatedMonthlySavings = recommendations.Sum(r => r.EstimatedMonthlySavings),
                totalEstimatedAnnualSavings = recommendations.Sum(r => r.EstimatedMonthlySavings) * 12,
                byType = recommendations
                    .GroupBy(r => r.Type)
                    .Select(g => new
                    {
                        type = g.Key,
                        count = g.Count(),
                        estimatedMonthlySavings = g.Sum(r => r.EstimatedMonthlySavings)
                    })
                    .OrderByDescending(x => x.estimatedMonthlySavings),
                bySubscription = recommendations
                    .GroupBy(r => r.SubscriptionId)
                    .Select(g => new
                    {
                        subscriptionId = g.Key,
                        count = g.Count(),
                        estimatedMonthlySavings = g.Sum(r => r.EstimatedMonthlySavings)
                    })
                    .OrderByDescending(x => x.estimatedMonthlySavings),
                recommendations = recommendations
                    .OrderByDescending(r => r.EstimatedMonthlySavings)
                    .Select(r => new
                    {
                        resourceId = r.ResourceId,
                        resourceName = r.ResourceName,
                        resourceType = r.ResourceType,
                        resourceGroup = r.ResourceGroup,
                        subscriptionId = r.SubscriptionId,
                        type = r.Type,
                        priority = r.Priority,
                        description = r.Description,
                        recommendation = r.Recommendation,
                        estimatedMonthlySavings = r.EstimatedMonthlySavings,
                        dailyCost = r.DailyCost,
                        estimatedMonthlyCost = r.EstimatedMonthlyCost,
                        confidence = r.Confidence,
                        impact = r.Impact
                    })
            };

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(summary);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FRONTEND-API] Erro ao buscar recomendações");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Erro interno ao buscar recomendações." });
            return errorResponse;
        }
    }
}
