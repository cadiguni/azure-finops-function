using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Application;
using Personal.FinOpsApi.AzureFunctions.Services;
using System.Net;

namespace Personal.FinOpsApi.AzureFunctions.Functions
{
    public class ManualTestFunction
    {
        private readonly ILogger<ManualTestFunction> _logger;
        private readonly SubscriptionDiscoveryService _discoveryService;
        private readonly CostAnalysisOrchestrator _orchestrator;
        private readonly QueueService _queueService; //  NOVO: Service Bus queue processing

        public ManualTestFunction(
            ILogger<ManualTestFunction> logger,
            SubscriptionDiscoveryService discoveryService,
            CostAnalysisOrchestrator orchestrator,
            QueueService queueService) //  NOVO: Queue service para processamento híbrido
        {
            _logger = logger;
            _discoveryService = discoveryService;
            _orchestrator = orchestrator;
            _queueService = queueService; //  NOVO
        }

        /// <summary>
        ///  TESTE MANUAL: Executa análise REAL em TODAS as subscriptions descobertas
        /// URL: https://[function-app].azurewebsites.net/api/ManualCostAnalysis
        /// </summary>
        [Function("ManualCostAnalysis")]
        public async Task<HttpResponseData> RunCostAnalysis(
            [HttpTrigger(AuthorizationLevel.Function, "post", "get")] HttpRequestData req)
        {
            _logger.LogInformation(" TESTE MANUAL: Análise de custo REAL - TODAS as subscriptions descobertas");

            try
            {
                var startTime = DateTime.UtcNow;
                
                //  DESCOBRIR subscriptions automaticamente
                var subscriptions = await _discoveryService.DiscoverSubscriptionsAsync();
                _logger.LogInformation(" Descobertas {count} subscriptions para análise manual", subscriptions.Count);

                var results = new List<object>();
                
                //  HÍBRIDO: Verificar se deve usar Service Bus ou execução direta
                if (_queueService.IsQueueProcessingEnabled)
                {
                    _logger.LogInformation(" [MANUAL-HÍBRIDO] Enviando {count} subscriptions para SERVICE BUS QUEUES", subscriptions.Count);
                    
                    //  Enviar cada subscription para queue
                    foreach (var subscriptionId in subscriptions)
                    {
                        try
                        {
                            var queueSent = await _queueService.SendSubscriptionAnalysisAsync(subscriptionId, "manual-test");
                            
                            if (queueSent)
                            {
                                results.Add(new { 
                                    subscription_id = subscriptionId, 
                                    status = "queued",
                                    message = "Análise enviada para processamento via Service Bus"
                                });
                                _logger.LogInformation(" [MANUAL-QUEUE] Subscription {subscriptionId} enviada para queue", subscriptionId);
                            }
                            else
                            {
                                _logger.LogWarning(" Falha ao enviar {subscriptionId} para queue - executando direto", subscriptionId);
                                await ExecuteRealAnalysisAsync(subscriptionId, _orchestrator);
                                results.Add(new { 
                                    subscription_id = subscriptionId, 
                                    status = "completed_direct",
                                    message = "Falha na queue - executado diretamente"
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, " Erro no processamento híbrido para {subscriptionId}", subscriptionId);
                            results.Add(new { 
                                subscription_id = subscriptionId, 
                                status = "error",
                                message = ex.Message
                            });
                        }
                    }
                }
                else
                {
                    _logger.LogInformation(" [MANUAL-DIRETO] Executando análises DIRETAMENTE (Service Bus desabilitado)");
                    
                    //  PROCESSAR cada subscription diretamente
                    foreach (var subscriptionId in subscriptions)
                    {
                        _logger.LogInformation(" Processando subscription manual: {subscriptionId}", subscriptionId);
                        
                        try
                        {
                            //  EXECUTAR análise REAL (mesmo código do Timer)
                            await ExecuteRealAnalysisAsync(subscriptionId, _orchestrator);
                            
                            results.Add(new { 
                                subscription_id = subscriptionId, 
                                status = "success",
                                message = "Análise executada com sucesso (direto)"
                            });
                            
                            _logger.LogInformation(" Subscription {subscriptionId} processada com sucesso", subscriptionId);
                        }
                        catch (Exception subEx)
                        {
                            _logger.LogError(subEx, " Erro ao processar subscription {subscriptionId}", subscriptionId);
                            results.Add(new { 
                                subscription_id = subscriptionId, 
                                status = "error",
                                message = subEx.Message
                            });
                        }
                    }
                }

                var executionTime = DateTime.UtcNow - startTime;
                _logger.LogInformation(" ANÁLISE MANUAL concluída - {count} subscriptions em {duration}ms", subscriptions.Count, executionTime.TotalMilliseconds);
                
                var response = req.CreateResponse(HttpStatusCode.OK);
                response.Headers.Add("Content-Type", "application/json");

                var result = new
                {
                    timestamp = DateTime.UtcNow,
                    total_subscriptions = subscriptions.Count,
                    execution_time_ms = executionTime.TotalMilliseconds,
                    processing_mode = _queueService.IsQueueProcessingEnabled ? "SERVICE_BUS_QUEUES" : "DIRECT_EXECUTION",
                    queue_processing_enabled = _queueService.IsQueueProcessingEnabled,
                    results = results
                };

                await response.WriteStringAsync(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, " TESTE MANUAL: Erro ao iniciar análise");
                
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync($" Erro: {ex.Message}");
                return errorResponse;
            }
        }

        /// <summary>
        ///  Executa análise REAL para uma subscription (mesmo processo do Timer)
        /// </summary>
        private async Task ExecuteRealAnalysisAsync(string subscriptionId, CostAnalysisOrchestrator orchestrator)
        {
            var dayOfWeek = DateTime.UtcNow.DayOfWeek;
            
            //  ANÁLISES DIÁRIAS (executam sempre) - ANÁLISE REAL
            _logger.LogInformation(" Executando análises DIÁRIAS para subscription {subscriptionId}...", subscriptionId);
            
            //  ANÁLISE REAL: Chama o orchestrator igual ao Timer
            await orchestrator.RunDailyAnalysisAsync(subscriptionId);
            
            _logger.LogInformation(" Análises DIÁRIAS concluídas para {subscriptionId}", subscriptionId);

            //  TESTE MANUAL: SEMPRE executar análises quinzenais (Storage, VMs, App Services)
            _logger.LogInformation(" TESTE MANUAL: Executando análises QUINZENAIS para subscription {subscriptionId}... (inclui Storage Accounts!)", subscriptionId);
            
            //  ANÁLISE REAL QUINZENAL: Forçar execução no teste manual
            await orchestrator.RunBiWeeklyAnalysisAsync(subscriptionId);
            
            _logger.LogInformation(" Análises QUINZENAIS concluídas para {subscriptionId} (Storage Accounts analisados!)", subscriptionId);
        }

    }
}
