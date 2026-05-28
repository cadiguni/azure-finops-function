using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Analyzers;
using Personal.FinOpsApi.AzureFunctions.Services;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// Processa analises de Storage Account via fila Service Bus.
/// </summary>
public class StorageAnalysisQueueFunction
{
    private readonly StorageAccountAnalyzer _storageAnalyzer;
    private readonly QueueService _queueService;
    private readonly AnalysisStorageService _storageService;
    private readonly ILogger<StorageAnalysisQueueFunction> _logger;

    public StorageAnalysisQueueFunction(
        StorageAccountAnalyzer storageAnalyzer,
        QueueService queueService,
        AnalysisStorageService storageService,
        ILogger<StorageAnalysisQueueFunction> logger)
    {
        _storageAnalyzer = storageAnalyzer;
        _queueService = queueService;
        _storageService = storageService;
        _logger = logger;
    }

    /// <summary>
    /// Processa mensagem da fila de analise de storage.
    /// </summary>
    [Function("StorageAnalysisQueue")]
    public async Task ProcessStorageAnalysis(
        [ServiceBusTrigger("storage-analysis", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message)
    {
        var messageId = message.MessageId;
        var startTime = DateTime.UtcNow;
        
        _logger.LogInformation("[STORAGE QUEUE] Iniciando analise - Message ID: {messageId}", messageId);

        // Parse da mensagem fora do try interno para uso nos catches.
        StorageAnalysisRequest? analysisRequest = null;
        
        try
        {
            var messageBody = message.Body.ToString();
            analysisRequest = JsonSerializer.Deserialize<StorageAnalysisRequest>(messageBody);
            
            if (analysisRequest == null || string.IsNullOrEmpty(analysisRequest.SubscriptionId))
            {
                _logger.LogError("Mensagem invalida para analise de storage");
                throw new ArgumentException("Invalid storage analysis message format");
            }

            var subscriptionId = analysisRequest.SubscriptionId;
            
            _logger.LogInformation("[STORAGE QUEUE] Analisando Storage Accounts para subscription {subscriptionId}", subscriptionId);

            // Timeout de 9 minutos para manter margem do limite total da fila.
            using var timeoutCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(9));

            // Executa analise principal.
            var analysisResult = await ExecuteStorageAnalysisWithTimeoutAsync(subscriptionId, timeoutCancellation.Token);

            if (analysisResult != null)
            {
                // Salva resultado localmente.
                await SaveStorageAnalysisResultsAsync(analysisResult, subscriptionId, startTime);

                // Envia resultado para fila de consolidacao.
                await _queueService.SendAnalysisResultsAsync(analysisResult, "storage", subscriptionId);

                var executionTime = DateTime.UtcNow - startTime;
                _logger.LogInformation("[STORAGE QUEUE] Analise concluida para {subscriptionId} - {findings} findings em {duration}ms", 
                    subscriptionId, analysisResult.Findings.Count, executionTime.TotalMilliseconds);
            }
            else
            {
                _logger.LogWarning("[STORAGE QUEUE] Analise retornou resultado nulo para {subscriptionId}", subscriptionId);
            }
        }
        catch (RateLimitedException ex)
        {
            _logger.LogWarning("[STORAGE QUEUE] Rate limit detectado - reagendando mensagem: {error}", ex.Message);
            if (analysisRequest != null) await RescheduleAsync(message, analysisRequest);
            return;
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("429"))
        {
            _logger.LogWarning("[STORAGE QUEUE] Rate limit HTTP 429 detectado - reagendando mensagem: {error}", ex.Message);
            if (analysisRequest != null) await RescheduleAsync(message, analysisRequest);
            return;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning("[STORAGE QUEUE] Timeout detectado - reagendando mensagem: {error}", ex.Message);
            if (analysisRequest != null) await RescheduleAsync(message, analysisRequest);
            return;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning("[STORAGE QUEUE] Timeout de operacao - reagendando mensagem: {error}", ex.Message);
            if (analysisRequest != null) await RescheduleAsync(message, analysisRequest);
            return;
        }
        catch (HttpRequestException ex) when (IsTransientError(ex))
        {
            _logger.LogWarning("[STORAGE QUEUE] Erro transitorio detectado - reagendando mensagem: {error}", ex.Message);
            if (analysisRequest != null) await RescheduleAsync(message, analysisRequest);
            return;
        }
        catch (OperationCanceledException)
        {
            var executionTime = DateTime.UtcNow - startTime;
            _logger.LogWarning("[STORAGE QUEUE] Timeout de 9 minutos atingido para message {messageId} apos {duration}ms", 
                messageId, executionTime.TotalMilliseconds);
            
            throw;
        }
        catch (Exception ex)
        {
            var executionTime = DateTime.UtcNow - startTime;
            _logger.LogError(ex, "[STORAGE QUEUE] Erro na analise de Storage - Message ID: {messageId} apos {duration}ms", 
                messageId, executionTime.TotalMilliseconds);
            
            throw;
        }
    }

    /// <summary>
    /// Executa analise de storage com timeout.
    /// </summary>
    private async Task<Models.StandardAnalyzerResult?> ExecuteStorageAnalysisWithTimeoutAsync(
        string subscriptionId, 
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("[STORAGE] Executando analise com timeout para {subscriptionId}", subscriptionId);

            var result = await _storageAnalyzer.AnalyzeSubscriptionAsync(
                subscriptionId, 
                analysisPeriodDays: 30, 
                dryRun: false
            );

            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("[STORAGE] Analise concluida - {findings} findings para {subscriptionId}", 
                result.Findings.Count, subscriptionId);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[STORAGE] Analise cancelada por timeout para {subscriptionId}", subscriptionId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[STORAGE] Erro na execucao da analise para {subscriptionId}", subscriptionId);
            throw;
        }
    }

    /// <summary>
    /// Salva resultados da analise de Storage Account.
    /// </summary>
    private async Task SaveStorageAnalysisResultsAsync(
        Models.StandardAnalyzerResult analysisResult, 
        string subscriptionId, 
        DateTime startTime)
    {
        try
        {
            // Prepara payload para persistencia.
            var storageResults = new
            {
                subscription_id = subscriptionId,
                analysis_date = startTime.ToString("yyyy-MM-dd"),
                analysis_timestamp = startTime,
                analysis_type = "storage-queue",
                analyzer = "StorageAccountAnalyzer",
                total_findings = analysisResult.Findings.Count,
                execution_metadata = analysisResult.ExecutionMetadata,
                findings = analysisResult.Findings
            };

            await _storageService.SaveAsync(subscriptionId, storageResults, startTime);
            
            _logger.LogInformation("[STORAGE] Resultados salvos no storage para {subscriptionId}", subscriptionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[STORAGE] Erro ao salvar resultados para {subscriptionId}", subscriptionId);
            // Falha ao salvar nao deve falhar toda a analise.
        }
    }

    /// <summary>
    /// Reagenda mensagem em caso de rate limit ou timeout.
    /// Política: retry 0→2min, 1→5min, 2→15min, 3+→30min
    /// Máximo 6 reagendamentos antes de DLQ manual
    /// </summary>
    private async Task RescheduleAsync(ServiceBusReceivedMessage message, StorageAnalysisRequest request)
    {
        try
        {
            // Extrai contador de tentativa.
            var retryCount = 0;
            if (message.ApplicationProperties.TryGetValue("retryCount", out var v) && v is int i)
                retryCount = i;

            if (retryCount >= 6)
            {
                _logger.LogError("[STORAGE QUEUE] Maximo de retries ({retryCount}) atingido - enviando para DLQ manual", retryCount);
                return;
            }

            var delayMinutes = retryCount switch
            {
                0 => 2,   // 2 minutos
                1 => 5,   // 5 minutos  
                2 => 15,  // 15 minutos
                3 => 30,  // 30 minutos
                _ => 30   // 30 minutos (máximo)
            };

            var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(delayMinutes);
            
            _logger.LogInformation("[STORAGE QUEUE] Reagendando mensagem para {scheduledTime} (delay: {delay}min, retry: {retry})", 
                scheduledTime, delayMinutes, retryCount + 1);

            // Reagenda mensagem com metadados de retry.
            var messageBody = JsonSerializer.Serialize(request);
            var properties = new Dictionary<string, object>
            {
                ["retryCount"] = retryCount + 1,
                ["originalEnqueueTime"] = message.EnqueuedTime,
                ["rateLimitRescheduled"] = true,
                ["originalMessageId"] = message.MessageId
            };
            
            await _queueService.ScheduleMessageAsync("storage-analysis", messageBody, scheduledTime, properties);
            
            _logger.LogInformation("[STORAGE QUEUE] Mensagem reagendada com sucesso - retry {retryCount}/6", retryCount + 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[STORAGE QUEUE] Erro ao reagendar mensagem: {error}", ex.Message);
        }
    }

    /// <summary>
    /// Detecta se erro HTTP e transitorio.
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

/// <summary>
/// Modelo da mensagem para analise de storage.
/// </summary>
public class StorageAnalysisRequest
{
    public string SubscriptionId { get; set; } = "";
    public List<string> StorageAccountIds { get; set; } = new();
    public string AnalysisType { get; set; } = "storage";
    public DateTime Timestamp { get; set; }
    public string RequestId { get; set; } = "";
}
