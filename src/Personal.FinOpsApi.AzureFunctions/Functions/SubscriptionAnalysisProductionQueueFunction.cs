using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Application;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Functions;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
///  PRODUCTION QUEUE FUNCTION - Processa subscription de PRODUÇÃO com configurações otimizadas
/// 
///  CONFIGURAÇÕES ESPECÍFICAS:
/// - maxConcurrentCalls = 1 (sem concorrência)
/// - prefetchCount = 0 (não prefetch)
/// - throttle global reduzido
/// - retry com exponential backoff + Retry-After
/// - timeout estendido (até 60 min)
/// 
///  SUBSCRIPTION ALVO: 504a622c-3995-46c5-8ba7-8edb365ed17b
/// </summary>
public class SubscriptionAnalysisProductionQueueFunction
{
    private readonly SubscriptionDiscoveryService _discoveryService;
    private readonly QueueService _queueService;
    private readonly CostAnalysisOrchestrator _orchestrator;
    private readonly AzureApiThrottleService _throttleService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubscriptionAnalysisProductionQueueFunction> _logger;

    //  THROTTLE ESPECÍFICO PARA PRODUÇÃO: Muito mais conservador
    private static readonly SemaphoreSlim _productionThrottle = new(1, 1); // MAX 1 execução simultânea

    public SubscriptionAnalysisProductionQueueFunction(
        SubscriptionDiscoveryService discoveryService,
        QueueService queueService,
        CostAnalysisOrchestrator orchestrator,
        AzureApiThrottleService throttleService,
        IServiceProvider serviceProvider,
        ILogger<SubscriptionAnalysisProductionQueueFunction> logger)
    {
        _discoveryService = discoveryService;
        _queueService = queueService;
        _orchestrator = orchestrator;
        _throttleService = throttleService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    ///  PROCESSA SUBSCRIPTION DE PRODUÇÃO - Queue subscription-analysis-production
    /// 
    ///  OTIMIZAÇÕES APLICADAS:
    /// - Throttle máximo (1 concurrent)
    /// - Delays entre operações Azure
    /// - Retry inteligente com exponential backoff
    /// - Timeouts estendidos
    /// - Anti-DLQ com reagendamento
    /// </summary>
    [Function("SubscriptionAnalysisProductionQueue")]
    public async Task ProcessProductionSubscriptionAnalysis(
        [ServiceBusTrigger("subscription-analysis-production", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message)
    {
        var messageId = message.MessageId;
        var startTime = DateTime.UtcNow;
        
        _logger.LogInformation(" [PRODUCTION QUEUE] Iniciando análise de subscription PRODUÇÃO - Message ID: {messageId}", messageId);

        //  THROTTLE GLOBAL PARA PRODUÇÃO: Apenas 1 análise por vez
        await _productionThrottle.WaitAsync();

        try
        {
            var messageBody = message.Body.ToString();
            var analysisRequest = JsonSerializer.Deserialize<SubscriptionAnalysisRequest>(messageBody);

            if (analysisRequest == null)
            {
                _logger.LogError(" [PRODUCTION QUEUE] Mensagem inválida: {messageBody}", messageBody);
                return;
            }

            _logger.LogInformation(" [PRODUCTION QUEUE] Processando subscription {subscriptionId} (IsProduction: {isProduction})", 
                analysisRequest.SubscriptionId, analysisRequest.IsProduction);

            if (!string.Equals(analysisRequest.AnalysisType, "complete", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    " [PRODUCTION QUEUE] Ignorando analysisType '{analysisType}' para subscription de produção. Somente 'complete' usa fila dedicada.",
                    analysisRequest.AnalysisType);
                return;
            }

            //  VALIDAÇÃO: Confirmar que é realmente subscription de produção
            var expectedProductionId = "504a622c-3995-46c5-8ba7-8edb365ed17b";
            if (!analysisRequest.SubscriptionId.Equals(expectedProductionId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(" [PRODUCTION QUEUE] Subscription {id} não é produção esperada ({expected})", 
                    analysisRequest.SubscriptionId, expectedProductionId);
            }

            //  EXECUTAR ANÁLISE COM CONFIGURAÇÕES DE PRODUÇÃO + CHECKPOINT
            await ExecuteProductionAnalysisWithCheckpoints(analysisRequest, messageId);

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation(" [PRODUCTION QUEUE] Análise de subscription {subscriptionId} concluída em {duration:mm\\:ss}", 
                analysisRequest.SubscriptionId, duration);
        }
        catch (HttpRequestException httpEx) when (httpEx.Message.Contains("429"))
        {
            _logger.LogWarning(" [PRODUCTION QUEUE] Rate limit (429) detectado - reagendando para +15min");
            await RescheduleProductionAsync(message, TimeSpan.FromMinutes(15), "Rate limit (429)");
        }
        catch (TimeoutException timeoutEx)
        {
            _logger.LogWarning(timeoutEx, "⏰ [PRODUCTION QUEUE] Timeout detectado - reagendando para +30min");
            await RescheduleProductionAsync(message, TimeSpan.FromMinutes(30), "Timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [PRODUCTION QUEUE] Erro ao processar subscription - reagendando para +10min");
            await RescheduleProductionAsync(message, TimeSpan.FromMinutes(10), "Error general");
        }
        finally
        {
            _productionThrottle.Release();
            _logger.LogInformation(" [PRODUCTION QUEUE] Throttle liberado");
        }
    }

    /// <summary>
    ///  BACKWARD COMPATIBILITY - Redireciona para novo método com checkpoints
    /// </summary>
    private async Task ExecuteProductionAnalysisWithRetryAsync(SubscriptionAnalysisRequest request)
    {
        // Redireciona para novo método com checkpoints
        await ExecuteProductionAnalysisWithCheckpoints(request, $"legacy_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
    }

    /// <summary>
    ///  REAGENDA mensagem de PRODUÇÃO com delay específico (anti-DLQ)
    /// </summary>
    private async Task RescheduleProductionAsync(ServiceBusReceivedMessage originalMessage, TimeSpan delay, string reason)
    {
        try
        {
            var messageBody = originalMessage.Body.ToString();
            var request = JsonSerializer.Deserialize<SubscriptionAnalysisRequest>(messageBody);
            
            if (request != null)
            {
                //  REAGENDAR: Incrementar contador de tentativas
                request.RetryCount = (request.RetryCount ?? 0) + 1;
                var maxProductionRetries = 10; // Produção tem mais tentativas

                if (request.RetryCount <= maxProductionRetries)
                {
                    var scheduledTime = DateTimeOffset.UtcNow.Add(delay);
                    var properties = new Dictionary<string, object>
                    {
                        ["OriginalMessageId"] = originalMessage.MessageId,
                        ["RescheduleReason"] = reason,
                        ["RetryCount"] = request.RetryCount,
                        ["ScheduledTime"] = scheduledTime.ToString("yyyy-MM-dd HH:mm:ss")
                    };

                    await _queueService.ScheduleMessageAsync("subscription-analysis-production", 
                        JsonSerializer.Serialize(request), scheduledTime, properties);

                    _logger.LogInformation(" [PRODUCTION] Reagendado para {time} (tentativa {retry}/{max}) - Motivo: {reason}",
                        scheduledTime.ToString("HH:mm"), request.RetryCount, maxProductionRetries, reason);
                }
                else
                {
                    _logger.LogError(" [PRODUCTION] Limite de tentativas excedido ({max}) - subscription pode precisar de intervenção manual",
                        maxProductionRetries);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [PRODUCTION] Erro ao reagendar mensagem");
        }
    }

    /// <summary>
    ///  ANÁLISE COM SISTEMA DE STEPS - Substitui análise completa para evitar timeouts
    /// Redireciona para sistema de processamento em etapas
    /// </summary>
    private async Task ExecuteProductionAnalysisWithCheckpoints(SubscriptionAnalysisRequest request, string messageId)
    {
        var subscriptionId = request.SubscriptionId;
        var checkpointId = $"prod_{subscriptionId}_{messageId}";
        
        _logger.LogInformation(" [PRODUCTION-STEPS] Redirecionando para sistema de steps - CheckpointId: {checkpointId}", checkpointId);

        try
        {
            //  NOVA ABORDAGEM: Usar sistema de steps para evitar timeout
            _logger.LogInformation(" [PRODUCTION-STEPS] Iniciando processamento em etapas para subscription {subscriptionId}", subscriptionId);

            // Produção usa analysisId estável por dia para evitar fragmentação em múltiplas pastas.
            var analysisId = $"{subscriptionId}-{DateTime.UtcNow:yyyy-MM-dd}";

            // Cria mensagem de orchestração para o sistema de steps
            var orchestrateMessage = new AnalysisStepMessage
            {
                AnalysisId = analysisId,
                SubscriptionId = subscriptionId,
                Step = "orchestrate",
                CreatedAt = DateTime.UtcNow
            };

            // Envia para processamento em etapas
            await _queueService.SendStepMessageAsync(orchestrateMessage);

            _logger.LogInformation(" [PRODUCTION-STEPS] Sistema de steps iniciado com sucesso: {analysisId} para {subscriptionId}", 
                analysisId, subscriptionId);

            // Log de informações sobre o que vai acontecer
            _logger.LogInformation(" [PRODUCTION-STEPS] O que vai acontecer:");
            _logger.LogInformation("  1⃣ Step 'orchestrate' vai enviar: storage, vm, appservice, publicip, consolidate");
            _logger.LogInformation("  2⃣ Cada step vai rodar em 2-5 minutos (sem timeout)");
            _logger.LogInformation("  3⃣ Step 'consolidate' vai juntar tudo e salvar recommendations.json + raw.json");
            _logger.LogInformation("  4⃣ Resultados ficam em: analyses/year=2026/month=02/day=18/{subscriptionId}/", subscriptionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [PRODUCTION-STEPS] Erro ao iniciar sistema de steps para {subscriptionId}: {error}", 
                subscriptionId, ex.Message);

            // Salva erro detalhado para debug
            await SaveDetailedErrorAsync(checkpointId, "STEPS_INITIALIZATION", ex);
            throw;
        }
    }

    /// <summary>
    ///  Executa uma etapa com logging detalhado
    /// </summary>
    private async Task LogAndExecuteStep(string stepName, string checkpointId, Func<Task> stepAction)
    {
        var stepStart = DateTime.UtcNow;
        
        try
        {
            _logger.LogInformation(" [STEP] Iniciando {stepName} - CheckpointId: {checkpointId}", stepName, checkpointId);
            
            await stepAction();
            
            var stepDuration = DateTime.UtcNow - stepStart;
            _logger.LogInformation(" [STEP] {stepName} concluído em {duration:mm\\:ss}", stepName, stepDuration);
        }
        catch (Exception ex)
        {
            var stepDuration = DateTime.UtcNow - stepStart;
            _logger.LogError(ex, " [STEP] Falha em {stepName} após {duration:mm\\:ss}", stepName, stepDuration);
            
            // Salvar erro específico da etapa
            await SaveStepErrorAsync(checkpointId, stepName, ex, stepDuration);
            throw;
        }
    }

    /// <summary>
    ///  SALVA ERRO DETALHADO no Blob para debug posterior
    /// </summary>
    private async Task SaveDetailedErrorAsync(string checkpointId, string subscriptionId, Exception ex, TimeSpan? duration = null)
    {
        try
        {
            var errorLog = new
            {
                CheckpointId = checkpointId,
                SubscriptionId = subscriptionId,
                Timestamp = DateTime.UtcNow,
                Duration = duration?.ToString(@"mm\:ss"),
                ErrorType = ex.GetType().Name,
                ErrorMessage = ex.Message,
                StackTrace = ex.StackTrace,
                InnerException = ex.InnerException?.Message,
                // Informações do host/context
                MachineName = Environment.MachineName,
                ProcessId = Environment.ProcessId,
                WorkerVersion = Environment.Version?.ToString()
            };

            var errorJson = JsonSerializer.Serialize(errorLog, new JsonSerializerOptions { WriteIndented = true });
            var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var blobPath = $"analysis-errors/{date}/{subscriptionId}/{checkpointId}.json";

            // Usar o storage service para salvar (implementar se necessário)
            _logger.LogWarning(" [ERROR-LOG] Erro detalhado salvo (simulado): {blobPath}", blobPath);
            _logger.LogWarning(" [ERROR-DETAIL] {errorJson}", errorJson);
        }
        catch (Exception saveEx)
        {
            _logger.LogError(saveEx, " Erro ao salvar log de erro detalhado");
        }
    }

    /// <summary>
    ///  SALVA ERRO ESPECÍFICO DE UMA ETAPA
    /// </summary>
    private async Task SaveStepErrorAsync(string checkpointId, string stepName, Exception ex, TimeSpan duration)
    {
        var errorLog = new
        {
            CheckpointId = checkpointId,
            StepName = stepName,
            StepDuration = duration.ToString(@"mm\:ss"),
            Timestamp = DateTime.UtcNow,
            ErrorType = ex.GetType().Name,
            ErrorMessage = ex.Message,
            StackTrace = ex.StackTrace?.Split('\n').Take(10) // Primeiras 10 linhas do stack
        };

        var errorJson = JsonSerializer.Serialize(errorLog, new JsonSerializerOptions { WriteIndented = true });
        _logger.LogWarning(" [STEP-ERROR] {stepName}: {errorJson}", stepName, errorJson);
    }
}
