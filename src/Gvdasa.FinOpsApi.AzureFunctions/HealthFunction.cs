using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Gvdasa.FinOpsApi.AzureFunctions.Application;
using Gvdasa.FinOpsApi.AzureFunctions.Analyzers;
using Gvdasa.FinOpsApi.AzureFunctions.Models;

namespace Gvdasa.FinOpsApi.AzureFunctions;

public class CostAnalysisFunctions
{
    private readonly HttpClient _httpClient;

    public CostAnalysisFunctions()
    {
        _httpClient = new HttpClient();
    }

    [Function("health")]
    public HttpResponseData Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.WriteString("OK - FinOps Cost Analysis API 🚀");
        return response;
    }

    [Function("analyze-costs")]
    public async Task<HttpResponseData> AnalyzeCosts(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
    {
        try
        {
            // Parse request
            CostAnalysisRequest request;
            
            if (req.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                request = JsonSerializer.Deserialize<CostAnalysisRequest>(requestBody, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new CostAnalysisRequest();
            }
            else
            {
                // GET request - usar query parameters ou default
                var subscriptionId = req.Query["subscriptionId"];
                request = new CostAnalysisRequest
                {
                    Scope = "subscription",
                    SubscriptionId = subscriptionId,
                    Include = new AnalysisIncludeOptions { UnattachedDisks = true }
                };
            }

            // Criar dependências
            var diskAnalyzer = new UnattachedDiskAnalyzer(_httpClient);
            var orchestrator = new CostAnalysisOrchestrator(diskAnalyzer);

            // Executar análise
            var result = await orchestrator.ExecuteAnalysisAsync(request);

            // Resposta
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            
            await response.WriteStringAsync(json);
            return response;
        }
        catch (Exception ex)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            errorResponse.Headers.Add("Content-Type", "application/json");
            
            var errorResult = new
            {
                error = true,
                message = ex.Message,
                timestamp = DateTime.UtcNow
            };
            
            var errorJson = JsonSerializer.Serialize(errorResult, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            
            await errorResponse.WriteStringAsync(errorJson);
            return errorResponse;
        }
    }

    // Manter função legada para compatibilidade
    [Function("collect-costs")]
    public async Task<HttpResponseData> CollectCosts(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
    {
        try
        {
            // Criar configuração padrão
            var request = new CostAnalysisRequest
            {
                Scope = "subscription",
                SubscriptionId = req.Query["subscriptionId"],
                Include = new AnalysisIncludeOptions { UnattachedDisks = true }
            };

            // Criar dependências
            var diskAnalyzer = new UnattachedDiskAnalyzer(_httpClient);
            var orchestrator = new CostAnalysisOrchestrator(diskAnalyzer);

            // Executar análise
            var result = await orchestrator.ExecuteAnalysisAsync(request);

            // Resposta
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            
            await response.WriteStringAsync(json);
            return response;
        }
        catch (Exception ex)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            errorResponse.Headers.Add("Content-Type", "application/json");
            
            var errorResult = new
            {
                error = true,
                message = ex.Message,
                timestamp = DateTime.UtcNow
            };
            
            var errorJson = JsonSerializer.Serialize(errorResult, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            
            await errorResponse.WriteStringAsync(errorJson);
            return errorResponse;
        }
    }
}