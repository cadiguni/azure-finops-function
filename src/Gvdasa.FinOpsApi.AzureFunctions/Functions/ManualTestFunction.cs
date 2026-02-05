using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Gvdasa.FinOpsApi.AzureFunctions.Services;
using System.Net;

namespace Gvdasa.FinOpsApi.AzureFunctions.Functions
{
    public class ManualTestFunction
    {
        private readonly ILogger<ManualTestFunction> _logger;
        private readonly QueueProcessingService _queueService;

        public ManualTestFunction(ILogger<ManualTestFunction> logger, QueueProcessingService queueService)
        {
            _logger = logger;
            _queueService = queueService;
        }

        /// <summary>
        /// ⚡ TESTE MANUAL: Trigger HTTP para testar análise de custo sem aguardar timer
        /// URL: https://[function-app].azurewebsites.net/api/test-cost-analysis
        /// </summary>
        [Function("ManualCostAnalysis")]
        public async Task<HttpResponseData> RunCostAnalysis(
            [HttpTrigger(AuthorizationLevel.Function, "post", "get")] HttpRequestData req)
        {
            _logger.LogInformation("🧪 TESTE MANUAL: Iniciando análise de custo via HTTP trigger");

            try
            {
                // Envia mensagem para a fila de análise - mesmo que o timer
                var subscriptionId = Environment.GetEnvironmentVariable("FinOps__SubscriptionId") ?? "";
                await _queueService.EnqueueSubscriptionAnalysisAsync(
                    new List<string> { subscriptionId }, 
                    "cost"
                );

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteStringAsync("✅ Análise de custo iniciada com sucesso! Verifique os logs e o storage em ~2-3 minutos.");
                
                _logger.LogInformation("✅ TESTE MANUAL: Análise enfileirada com sucesso");
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ TESTE MANUAL: Erro ao iniciar análise");
                
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync($"❌ Erro: {ex.Message}");
                return errorResponse;
            }
        }

        /// <summary>
        /// 🔍 VERIFICAR STATUS: Mostra informações sobre o ambiente e configuração
        /// URL: https://[function-app].azurewebsites.net/api/test-status
        /// </summary>
        [Function("TestStatus")]
        public async Task<HttpResponseData> GetStatus(
            [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequestData req)
        {
            _logger.LogInformation("🔍 TESTE MANUAL: Verificando status da configuração");

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");

            var status = new
            {
                timestamp = DateTime.UtcNow,
                subscription_id = Environment.GetEnvironmentVariable("FinOps__SubscriptionId"),
                tenant_id = Environment.GetEnvironmentVariable("FinOps__TenantId"),
                storage_account = Environment.GetEnvironmentVariable("FinOps__StorageAccountName"),
                client_id = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"),
                cron_schedule = Environment.GetEnvironmentVariable("FinOps__Schedules__CostAnalysis"),
                runtime_info = new
                {
                    dotnet_version = Environment.Version.ToString(),
                    worker_runtime = Environment.GetEnvironmentVariable("FUNCTIONS_WORKER_RUNTIME"),
                    extension_version = Environment.GetEnvironmentVariable("FUNCTIONS_EXTENSION_VERSION")
                }
            };

            await response.WriteStringAsync(System.Text.Json.JsonSerializer.Serialize(status, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return response;
        }
    }
}