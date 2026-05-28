using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Application;
using Personal.FinOpsApi.AzureFunctions.Models;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
///  QUEUE FUNCTION - Processa subscriptions e distribui análises específicas
///  HÍBRIDO: Recebe subscription → distribui para queues especializadas
/// </summary>
public class SubscriptionAnalysisQueueFunction
{
    private readonly SubscriptionDiscoveryService _discoveryService;
    private readonly QueueService _queueService;
    private readonly CostAnalysisOrchestrator _orchestrator;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubscriptionAnalysisQueueFunction> _logger;

    public SubscriptionAnalysisQueueFunction(
        SubscriptionDiscoveryService discoveryService,
        QueueService queueService,
        CostAnalysisOrchestrator orchestrator,
        IServiceProvider serviceProvider,
        ILogger<SubscriptionAnalysisQueueFunction> logger)
    {
        _discoveryService = discoveryService;
        _queueService = queueService;
        _orchestrator = orchestrator;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    ///  Processa mensagem da queue subscription-analysis
    /// Distribui análise de uma subscription para queues especializadas
    /// </summary>
    [Function("SubscriptionAnalysisQueue")]
    public async Task ProcessSubscriptionAnalysis(
        [ServiceBusTrigger("subscription-analysis", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message)
    {
        var messageId = message.MessageId;
        _logger.LogInformation(" [QUEUE] Processando análise de subscription - Message ID: {messageId}", messageId);

        try
        {
            //  Parse da mensagem
            var messageBody = message.Body.ToString();
            var analysisRequest = JsonSerializer.Deserialize<SubscriptionAnalysisRequest>(messageBody);
            
            if (analysisRequest == null || string.IsNullOrEmpty(analysisRequest.SubscriptionId))
            {
                _logger.LogError(" Mensagem inválida - subscription ID não encontrado");
                throw new ArgumentException("Invalid message format");
            }

            var subscriptionId = analysisRequest.SubscriptionId;
            var analysisType = analysisRequest.AnalysisType ?? "complete";

            _logger.LogInformation(" [QUEUE] Processando subscription {subscriptionId} - Tipo: {analysisType}", 
                subscriptionId, analysisType);

            try
            {
                _logger.LogInformation(" [QUEUE] Executando análise REAL via orchestrator para {subscriptionId}", subscriptionId);
                
                //  EXECUTAR ANÁLISE COMPLETA usando o orchestrator simplificado
                if (analysisType == "manual-test" || analysisType == "complete" || analysisType == "full")
                {
                    // Análise completa (todos os analyzers)
                    await _orchestrator.AnalyzeSubscriptionAsync(subscriptionId, "complete", false);
                    
                    _logger.LogInformation(" [QUEUE] Análise COMPLETA concluída para {subscriptionId}", subscriptionId);
                }
                else
                {
                    _logger.LogInformation("ℹ [QUEUE] Tipo de análise {analysisType} não suportado", analysisType);
                }
            }
            catch (RateLimitedException ex)
            {
                _logger.LogWarning(" [QUEUE] Rate limit detectado - reagendando mensagem: {error}", ex.Message);
                await RescheduleAsync(message, analysisRequest);
                return; //  IMPORTANTE: Não deixar exception estourar
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("429"))
            {
                _logger.LogWarning(" [QUEUE] Rate limit HTTP 429 detectado - reagendando mensagem: {error}", ex.Message);
                await RescheduleAsync(message, analysisRequest);
                return; //  IMPORTANTE: Não deixar exception estourar
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning("⏱ [QUEUE] Timeout detectado - reagendando mensagem: {error}", ex.Message);
                await RescheduleAsync(message, analysisRequest);
                return; //  IMPORTANTE: Não deixar exception estourar
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning("⏱ [QUEUE] Timeout de operação - reagendando mensagem: {error}", ex.Message);
                await RescheduleAsync(message, analysisRequest);
                return; //  IMPORTANTE: Não deixar exception estourar
            }
            catch (HttpRequestException ex) when (IsTransientError(ex))
            {
                _logger.LogWarning(" [QUEUE] Erro transitório detectado - reagendando mensagem: {error}", ex.Message);
                await RescheduleAsync(message, analysisRequest);
                return; //  IMPORTANTE: Não deixar exception estourar
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [QUEUE] Erro ao processar subscription analysis - Message ID: {messageId}", messageId);
            throw; // Re-throw para Service Bus retry policy
        }
    }

    /// <summary>
    ///  Reagenda mensagem quando der rate limit (429)
    /// Política: retry 0→2min, 1→5min, 2→15min, 3+→30min
    /// Máximo 6 reagendamentos antes de DLQ manual
    /// </summary>
    private async Task RescheduleAsync(ServiceBusReceivedMessage message, SubscriptionAnalysisRequest request)
    {
        try
        {
            //  Extrair retry count
            var retryCount = 0;
            if (message.ApplicationProperties.TryGetValue("retryCount", out var v) && v is int i)
                retryCount = i;

            //  Limite máximo de retries
            if (retryCount >= 6)
            {
                _logger.LogError(" Máximo de retries ({retryCount}) atingido - enviando para DLQ manual", retryCount);
                // Aqui poderia enviar para uma DLQ customizada se necessário
                return;
            }

            // ⏰ Calcular delay crescente
            var delayMinutes = retryCount switch
            {
                0 => 2,   // 2 minutos
                1 => 5,   // 5 minutos  
                2 => 15,  // 15 minutos
                3 => 30,  // 30 minutos
                _ => 30   // 30 minutos (máximo)
            };

            var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(delayMinutes);
            
            _logger.LogInformation(" Reagendando mensagem para {scheduledTime} (delay: {delay}min, retry: {retry})", 
                scheduledTime, delayMinutes, retryCount + 1);

            //  Reagendar mensagem com propriedades de retry
            var messageBody = JsonSerializer.Serialize(request);
            var properties = new Dictionary<string, object>
            {
                ["retryCount"] = retryCount + 1,
                ["originalEnqueueTime"] = message.EnqueuedTime,
                ["rateLimitRescheduled"] = true,
                ["originalMessageId"] = message.MessageId
            };
            
            await _queueService.ScheduleMessageAsync("subscription-analysis", messageBody, scheduledTime, properties);
            
            _logger.LogInformation(" Mensagem reagendada com sucesso - retry {retryCount}/6", retryCount + 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao reagendar mensagem: {error}", ex.Message);
        }
    }

    /// <summary>
    ///  HELPER: Detecta se um erro HTTP é transitório e deve ser reagendado
    /// </summary>
    private static bool IsTransientError(HttpRequestException ex)
    {
        var message = ex.Message.ToLowerInvariant();
        
        return message.Contains("502") ||  // Bad Gateway
               message.Contains("503") ||  // Service Unavailable  
               message.Contains("504") ||  // Gateway Timeout
               message.Contains("408") ||  // Request Timeout
               message.Contains("500") ||  // Internal Server Error (pode ser transitório)
               message.Contains("connection") ||  // Connection issues
               message.Contains("timeout") ||     // Timeout errors
               message.Contains("throttle");      // Throttling messages
    }
}