using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Analyzers;
using Personal.FinOpsApi.AzureFunctions.Services;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// 📦 STORAGE ANALYSIS QUEUE FUNCTION - Análise pesada via queue
/// 🎯 TIMEOUT: 10 minutos para Storage Account analysis via Azure Monitor
/// 🚀 PARALELISMO: Múltiplas instâncias processam subscriptions diferentes
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
    /// 📦 Processa análise de Storage Accounts via queue
    /// 🚨 TIMEOUT: 10 minutos (configurado no Service Bus)
    /// </summary>
    [Function("StorageAnalysisQueue")]
    public async Task ProcessStorageAnalysis(
        [ServiceBusTrigger("storage-analysis", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message)
    {
        var messageId = message.MessageId;
        var startTime = DateTime.UtcNow;
        
        _logger.LogInformation("📦 [STORAGE QUEUE] Iniciando análise - Message ID: {messageId}", messageId);

        // 📋 Parse da mensagem - FORA do try interno para ser acessível nos catches
        StorageAnalysisRequest? analysisRequest = null;
        
        try
        {
            var messageBody = message.Body.ToString();
            analysisRequest = JsonSerializer.Deserialize<StorageAnalysisRequest>(messageBody);
            
            if (analysisRequest == null || string.IsNullOrEmpty(analysisRequest.SubscriptionId))
            {
                _logger.LogError("❌ Mensagem inválida para Storage analysis");
                throw new ArgumentException("Invalid storage analysis message format");
            }

            var subscriptionId = analysisRequest.SubscriptionId;
            
            _logger.LogInformation("📦 [STORAGE QUEUE] Analisando Storage Accounts para subscription {subscriptionId}", subscriptionId);

            // 🚨 TIMEOUT PROTECTION: 9 minutos (deixa margem para o Service Bus 10min)
            using var timeoutCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(9));

            // 🔍 EXECUTAR ANÁLISE REAL usando o analyzer existente
            var analysisResult = await ExecuteStorageAnalysisWithTimeoutAsync(subscriptionId, timeoutCancellation.Token);

            if (analysisResult != null)
            {
                // 💾 Salvar resultados localmente
                await SaveStorageAnalysisResultsAsync(analysisResult, subscriptionId, startTime);

                // 📤 Enviar resultados para queue de consolidação  
                await _queueService.SendAnalysisResultsAsync(analysisResult, "storage", subscriptionId);

                var executionTime = DateTime.UtcNow - startTime;
                _logger.LogInformation("✅ [STORAGE QUEUE] Análise concluída para {subscriptionId} - {findings} findings em {duration}ms", 
                    subscriptionId, analysisResult.Findings.Count, executionTime.TotalMilliseconds);
            }
            else
            {
                _logger.LogWarning("⚠️ [STORAGE QUEUE] Análise retornou resultado nulo para {subscriptionId}", subscriptionId);
            }
        }
        catch (RateLimitedException ex)
        {
            _logger.LogWarning("🚫 [STORAGE QUEUE] Rate limit detectado - reagendando mensagem: {error}", ex.Message);
            if (analysisRequest != null) await RescheduleAsync(message, analysisRequest);
            return; // ✅ IMPORTANTE: Não deixar exception estourar
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("429"))
        {
            _logger.LogWarning("🚫 [STORAGE QUEUE] Rate limit HTTP 429 detectado - reagendando mensagem: {error}", ex.Message);
            if (analysisRequest != null) await RescheduleAsync(message, analysisRequest);
            return; // ✅ IMPORTANTE: Não deixar exception estourar
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning("⏱️ [STORAGE QUEUE] Timeout detectado - reagendando mensagem: {error}", ex.Message);
            if (analysisRequest != null) await RescheduleAsync(message, analysisRequest);
            return; // ✅ IMPORTANTE: Não deixar exception estourar
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning("⏱️ [STORAGE QUEUE] Timeout de operação - reagendando mensagem: {error}", ex.Message);
            if (analysisRequest != null) await RescheduleAsync(message, analysisRequest);
            return; // ✅ IMPORTANTE: Não deixar exception estourar
        }
        catch (HttpRequestException ex) when (IsTransientError(ex))
        {
            _logger.LogWarning("🔄 [STORAGE QUEUE] Erro transitório detectado - reagendando mensagem: {error}", ex.Message);
            if (analysisRequest != null) await RescheduleAsync(message, analysisRequest);
            return; // ✅ IMPORTANTE: Não deixar exception estourar
        }
        catch (OperationCanceledException)
        {
            var executionTime = DateTime.UtcNow - startTime;
            _logger.LogWarning("⏰ [STORAGE QUEUE] Timeout de 9 minutos atingido para message {messageId} após {duration}ms", 
                messageId, executionTime.TotalMilliseconds);
            
            // ❌ Re-throw para Service Bus marcar como falha e tentar retry (max 2x)
            throw;
        }
        catch (Exception ex)
        {
            var executionTime = DateTime.UtcNow - startTime;
            _logger.LogError(ex, "❌ [STORAGE QUEUE] Erro na análise de Storage - Message ID: {messageId} após {duration}ms", 
                messageId, executionTime.TotalMilliseconds);
            
            throw; // Re-throw para Service Bus retry policy
        }
    }

    /// <summary>
    /// 🔍 Executa análise de Storage com timeout protection
    /// </summary>
    private async Task<Models.StandardAnalyzerResult?> ExecuteStorageAnalysisWithTimeoutAsync(
        string subscriptionId, 
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("🔍 [STORAGE] Executando análise REAL com timeout protection para {subscriptionId}", subscriptionId);

            // 📊 ANÁLISE REAL: Usar o StorageAccountAnalyzer existente (otimizado)
            var result = await _storageAnalyzer.AnalyzeSubscriptionAsync(
                subscriptionId, 
                analysisPeriodDays: 30, 
                dryRun: false
            );

            // ✅ Verificar se foi cancelado
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("📊 [STORAGE] Análise concluída - {findings} findings para {subscriptionId}", 
                result.Findings.Count, subscriptionId);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("⏰ [STORAGE] Análise cancelada por timeout para {subscriptionId}", subscriptionId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [STORAGE] Erro na execução da análise para {subscriptionId}", subscriptionId);
            throw;
        }
    }

    /// <summary>
    /// 💾 Salva resultados da análise de Storage Account
    /// </summary>
    private async Task SaveStorageAnalysisResultsAsync(
        Models.StandardAnalyzerResult analysisResult, 
        string subscriptionId, 
        DateTime startTime)
    {
        try
        {
            // 📊 Preparar dados para salvar
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

            // 💾 Salvar usando o serviço existente
            await _storageService.SaveAsync(subscriptionId, storageResults, startTime);
            
            _logger.LogInformation("💾 [STORAGE] Resultados salvos no storage para {subscriptionId}", subscriptionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [STORAGE] Erro ao salvar resultados para {subscriptionId}", subscriptionId);
            // Não re-throw - falha no save não deve falhar a análise inteira
        }
    }

    /// <summary>
    /// 📅 Reagenda mensagem quando der rate limit ou timeout
    /// Política: retry 0→2min, 1→5min, 2→15min, 3+→30min
    /// Máximo 6 reagendamentos antes de DLQ manual
    /// </summary>
    private async Task RescheduleAsync(ServiceBusReceivedMessage message, StorageAnalysisRequest request)
    {
        try
        {
            // 🔢 Extrair retry count
            var retryCount = 0;
            if (message.ApplicationProperties.TryGetValue("retryCount", out var v) && v is int i)
                retryCount = i;

            // 🛑 Limite máximo de retries
            if (retryCount >= 6)
            {
                _logger.LogError("🛑 [STORAGE QUEUE] Máximo de retries ({retryCount}) atingido - enviando para DLQ manual", retryCount);
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
            
            _logger.LogInformation("📅 [STORAGE QUEUE] Reagendando mensagem para {scheduledTime} (delay: {delay}min, retry: {retry})", 
                scheduledTime, delayMinutes, retryCount + 1);

            // 📤 Reagendar mensagem com propriedades de retry
            var messageBody = JsonSerializer.Serialize(request);
            var properties = new Dictionary<string, object>
            {
                ["retryCount"] = retryCount + 1,
                ["originalEnqueueTime"] = message.EnqueuedTime,
                ["rateLimitRescheduled"] = true,
                ["originalMessageId"] = message.MessageId
            };
            
            await _queueService.ScheduleMessageAsync("storage-analysis", messageBody, scheduledTime, properties);
            
            _logger.LogInformation("✅ [STORAGE QUEUE] Mensagem reagendada com sucesso - retry {retryCount}/6", retryCount + 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [STORAGE QUEUE] Erro ao reagendar mensagem: {error}", ex.Message);
        }
    }

    /// <summary>
    /// 🔍 HELPER: Detecta se um erro HTTP é transitório e deve ser reagendado
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
/// 📋 Modelo da mensagem para análise de Storage
/// </summary>
public class StorageAnalysisRequest
{
    public string SubscriptionId { get; set; } = "";
    public List<string> StorageAccountIds { get; set; } = new();
    public string AnalysisType { get; set; } = "storage";
    public DateTime Timestamp { get; set; }
    public string RequestId { get; set; } = "";
}