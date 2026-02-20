using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Application;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// 🧪 TESTE DE TIMER - Executa análise manualmente via HTTP para debug
/// </summary>
public class TimerTestFunction
{
    private readonly ILogger<TimerTestFunction> _logger;
    private readonly CostAnalysisOrchestrator _orchestrator;

    public TimerTestFunction(
        ILogger<TimerTestFunction> logger,
        CostAnalysisOrchestrator orchestrator)
    {
        _logger = logger;
        _orchestrator = orchestrator;
    }

    [Function("test-timer")]
    public async Task<HttpResponseData> TestTimerAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req,
        FunctionContext context)
    {
        _logger.LogInformation("🧪 TESTE TIMER: Executando análise manual para debug...");

        try
        {
            // Testar se o CostAnalysisOrchestrator funciona com subscription padrão
            var testSubscriptionId = Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID") ?? "test-subscription";
            _logger.LogInformation("🧪 Testando com subscription: {subscriptionId}", testSubscriptionId);
            
            await _orchestrator.RunDailyAnalysisAsync(testSubscriptionId);
            
            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteStringAsync($"✅ Timer test executado com sucesso! Análise diária simulada para {testSubscriptionId}");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro no teste do timer: {error}", ex.Message);
            
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"❌ Erro no teste: {ex.Message}");
            return errorResponse;
        }
    }
}