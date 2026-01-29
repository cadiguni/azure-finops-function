using Gvdasa.GVmodeloexemploapi.Domain.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Functions;

public class CostAnalysisFunction
{
    private readonly ICostAnalysisOrchestrator _costAnalysisOrchestrator;
    private readonly ILogger<CostAnalysisFunction> _logger;

    public CostAnalysisFunction(ICostAnalysisOrchestrator costAnalysisOrchestrator, ILogger<CostAnalysisFunction> logger)
    {
        _costAnalysisOrchestrator = costAnalysisOrchestrator;
        _logger = logger;
    }

    [Function("CostAnalysisTimerTrigger")]
    public async Task RunScheduled([TimerTrigger("0 0 3 * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Iniciando análise de custo diária às {Time}", DateTime.Now);

        try
        {
            // Executar análise para todas as subscriptions (1x por dia é suficiente)
            // Custos não mudam de hora em hora, economiza processamento
            var result = await _costAnalysisOrchestrator.AnalyzeAllSubscriptionsAsync(30);
            
            _logger.LogInformation("Análise concluída com sucesso. {FindingCount} achados, economia potencial: {TotalSaving:C}", 
                result.TotalFindings, result.TotalPotentialSaving);

            // Aqui você pode adicionar lógica para:
            // - Salvar resultados em storage
            // - Enviar relatório por email
            // - Criar alertas para achados críticos
            // - Atualizar dashboard
            
            await LogResultsAsync(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro durante execução da análise programada");
            throw;
        }
    }

    [Function("CostAnalysisHttpTrigger")]
    public async Task<HttpResponseData> RunHttp(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
    {
        _logger.LogInformation("Recebida requisição HTTP para análise de custo");

        try
        {
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");

            // Parse dos parâmetros da requisição
            var requestParams = await ParseRequestParametersAsync(req);
            
            // Executar análise baseada nos parâmetros
            var result = await ExecuteAnalysisBasedOnParametersAsync(requestParams);
            
            // Serializar resultado
            var jsonResult = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });

            await response.WriteStringAsync(jsonResult);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro durante execução da análise via HTTP");
            
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new
            {
                error = "Erro interno durante análise",
                message = ex.Message,
                timestamp = DateTime.UtcNow
            }));
            return errorResponse;
        }
    }

    [Function("CostAnalysisSubscription")]
    public async Task<HttpResponseData> AnalyzeSubscription(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        _logger.LogInformation("Recebida requisição para análise de subscription específica");

        try
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var requestParams = JsonSerializer.Deserialize<SubscriptionAnalysisRequest>(requestBody);

            if (requestParams?.SubscriptionId == null)
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    error = "subscriptionId é obrigatório"
                }));
                return badResponse;
            }

            var result = await _costAnalysisOrchestrator.AnalyzeSubscriptionAsync(
                requestParams.SubscriptionId, 
                requestParams.AnalysisPeriodDays ?? 30);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            
            var jsonResult = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });

            await response.WriteStringAsync(jsonResult);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro durante análise de subscription");
            
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new
            {
                error = "Erro durante análise de subscription",
                message = ex.Message
            }));
            return errorResponse;
        }
    }

    private async Task<AnalysisRequestParameters> ParseRequestParametersAsync(HttpRequestData req)
    {
        var parameters = new AnalysisRequestParameters();
        
        // Parse query parameters
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        
        if (query["subscriptionId"] != null)
            parameters.SubscriptionId = query["subscriptionId"];
            
        if (query["resourceGroup"] != null)
            parameters.ResourceGroupName = query["resourceGroup"];
            
        if (int.TryParse(query["days"], out var days))
            parameters.AnalysisPeriodDays = days;
            
        if (bool.TryParse(query["dryRun"], out var dryRun))
            parameters.DryRun = dryRun;

        // Se for POST, tentar ler do body também
        if (req.Method == "POST")
        {
            try
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                if (!string.IsNullOrEmpty(body))
                {
                    var bodyParams = JsonSerializer.Deserialize<AnalysisRequestParameters>(body);
                    if (bodyParams != null)
                    {
                        parameters.SubscriptionId ??= bodyParams.SubscriptionId;
                        parameters.ResourceGroupName ??= bodyParams.ResourceGroupName;
                        parameters.AnalysisPeriodDays ??= bodyParams.AnalysisPeriodDays;
                        parameters.DryRun ??= bodyParams.DryRun;
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Erro ao deserializar body da requisição");
            }
        }

        return parameters;
    }

    private async Task<object> ExecuteAnalysisBasedOnParametersAsync(AnalysisRequestParameters parameters)
    {
        var analysisPeriod = parameters.AnalysisPeriodDays ?? 30;

        if (parameters.DryRun == true)
        {
            _logger.LogInformation("Modo DRY RUN ativado - apenas simulação");
            return new { message = "Dry run mode - análise simulada", parameters };
        }

        // Análise por resource group
        if (!string.IsNullOrEmpty(parameters.SubscriptionId) && !string.IsNullOrEmpty(parameters.ResourceGroupName))
        {
            return await _costAnalysisOrchestrator.AnalyzeResourceGroupAsync(
                parameters.SubscriptionId, parameters.ResourceGroupName, analysisPeriod);
        }

        // Análise por subscription
        if (!string.IsNullOrEmpty(parameters.SubscriptionId))
        {
            return await _costAnalysisOrchestrator.AnalyzeSubscriptionAsync(parameters.SubscriptionId, analysisPeriod);
        }

        // Análise de todas as subscriptions
        return await _costAnalysisOrchestrator.AnalyzeAllSubscriptionsAsync(analysisPeriod);
    }

    private async Task LogResultsAsync(object result)
    {
        // Implementar lógica para persistir resultados
        // Exemplos:
        // - Salvar JSON em Azure Storage
        // - Inserir sumário em database
        // - Enviar para Event Hub
        // - Criar arquivo de log estruturado
        
        _logger.LogInformation("Resultado da análise: {Result}", JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        }));
        
        await Task.CompletedTask; // Placeholder para implementação futura
    }
}

public class AnalysisRequestParameters
{
    public string? SubscriptionId { get; set; }
    public string? ResourceGroupName { get; set; }
    public int? AnalysisPeriodDays { get; set; }
    public bool? DryRun { get; set; }
}

public class SubscriptionAnalysisRequest
{
    public string? SubscriptionId { get; set; }
    public int? AnalysisPeriodDays { get; set; }
}