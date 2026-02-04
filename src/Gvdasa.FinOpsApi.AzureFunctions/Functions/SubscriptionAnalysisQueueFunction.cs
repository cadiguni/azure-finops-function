using Gvdasa.FinOpsApi.AzureFunctions.Services;
using Gvdasa.FinOpsApi.AzureFunctions.Analyzers;
using Gvdasa.FinOpsApi.AzureFunctions.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gvdasa.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// 🚀 QUEUE TRIGGER - Processamento paralelo de subscriptions
/// 
/// Arquitetura Enterprise:
/// Timer → Queue → Parallel Processing → Results
/// 
/// ✅ Escalabilidade horizontal automática
/// ✅ Paralelismo controlado por circuit breaker
/// ✅ Resiliente a falhas individuais
/// </summary>
public class SubscriptionAnalysisQueueFunction
{
    private readonly UnusedPublicIpAnalyzer _publicIpAnalyzer;
    private readonly UnattachedDiskAnalyzer _diskAnalyzer;
    private readonly IdleVmAnalyzer _vmAnalyzer;
    private readonly StorageAccountAnalyzer _storageAnalyzer;
    private readonly AppServiceAnalyzer _appServiceAnalyzer;
    private readonly CircuitBreakerService _circuitBreaker;
    private readonly ObservabilityService _observability;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SubscriptionAnalysisQueueFunction> _logger;

    public SubscriptionAnalysisQueueFunction(
        UnusedPublicIpAnalyzer publicIpAnalyzer,
        UnattachedDiskAnalyzer diskAnalyzer,
        IdleVmAnalyzer vmAnalyzer,
        StorageAccountAnalyzer storageAnalyzer,
        AppServiceAnalyzer appServiceAnalyzer,
        CircuitBreakerService circuitBreaker,
        ObservabilityService observability,
        IConfiguration configuration,
        ILogger<SubscriptionAnalysisQueueFunction> logger)
    {
        _publicIpAnalyzer = publicIpAnalyzer;
        _diskAnalyzer = diskAnalyzer;
        _vmAnalyzer = vmAnalyzer;
        _storageAnalyzer = storageAnalyzer;
        _appServiceAnalyzer = appServiceAnalyzer;
        _circuitBreaker = circuitBreaker;
        _observability = observability;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// 🔄 Processa análise individual de subscription via queue
    /// </summary>
    [Function("SubscriptionAnalysisQueue")]
    public async Task RunAsync(
        [QueueTrigger("subscription-analysis")] string queueMessage,
        FunctionContext context)
    {
        var startTime = DateTime.UtcNow;
        
        try
        {
            var message = System.Text.Json.JsonSerializer.Deserialize<SubscriptionAnalysisMessage>(queueMessage);
            if (message == null) return;

            _logger.LogInformation("🚀 Processando {type} para subscription {sub}", 
                message.AnalysisType, message.SubscriptionId);

            // 🎚️ Feature Flag Check
            if (!IsAnalysisEnabled(message.AnalysisType))
            {
                _logger.LogInformation("⚪ Análise {type} desabilitada via feature flag", message.AnalysisType);
                return;
            }

            // 🛡️ Circuit Breaker - ajusta paralelismo baseado na saúde
            var currentParallelism = _circuitBreaker.GetCurrentParallelism();
            _logger.LogDebug("🚦 Paralelismo atual: {parallelism}", currentParallelism);

            // 🎯 Executa analyzer específico
            var result = await ExecuteAnalysisAsync(message.SubscriptionId, message.AnalysisType);
            
            if (result != null)
            {
                var executionTime = DateTime.UtcNow - startTime;
                
                // 📊 Registra métricas de sucesso
                _observability.RecordAnalyzerExecutionTime(message.AnalysisType, executionTime, true);
                _observability.RecordSubscriptionProcessed(message.SubscriptionId, message.AnalysisType, result.Findings.Count);
                _observability.RecordSavingsFound(message.AnalysisType, result.Findings.Sum(f => f.EstimatedMonthlySavings));
                _circuitBreaker.RecordSuccess();

                _logger.LogInformation("✅ {type} concluída para {sub}: {findings} findings, R$ {savings:F2} economias em {duration}ms", 
                    message.AnalysisType, message.SubscriptionId, result.Findings.Count, 
                    result.Findings.Sum(f => f.EstimatedMonthlySavings), executionTime.TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            var executionTime = DateTime.UtcNow - startTime;
            
            // 📊 Registra métricas de falha
            _observability.RecordError("SubscriptionAnalysis", ex);
            _circuitBreaker.RecordFailure(ex);

            _logger.LogError(ex, "❌ Falha no processamento de queue: {error}", ex.Message);
            throw; // Re-throw para que o Azure reprocesse a mensagem
        }
    }

    /// <summary>
    /// 🎯 Executa análise específica baseada no tipo
    /// </summary>
    private async Task<StandardAnalyzerResult?> ExecuteAnalysisAsync(string subscriptionId, string analysisType)
    {
        return analysisType.ToLower() switch
        {
            "storage" => await _storageAnalyzer.AnalyzeSubscriptionAsync(subscriptionId, 30, false),
            "vm" => await _vmAnalyzer.AnalyzeAsync(subscriptionId, 7, false),
            "appservice" => await _appServiceAnalyzer.AnalyzeAsync(subscriptionId, 7, false),
            "publicip" => await _publicIpAnalyzer.AnalyzeAsync(subscriptionId, 7, false),
            "disk" => await _diskAnalyzer.AnalyzeSubscriptionAsync(subscriptionId, 7, false),
            _ => null
        };
    }

    /// <summary>
    /// 🎚️ Verifica se análise está habilitada via feature flag
    /// </summary>
    private bool IsAnalysisEnabled(string analysisType)
    {
        var featureFlagKey = analysisType.ToLower() switch
        {
            "storage" => "EnableStorageAnalyzer",
            "vm" => "EnableVmAnalyzer",
            "appservice" => "EnableAppServiceAnalyzer",
            "publicip" => "EnablePublicIpAnalyzer",
            "disk" => "EnableDiskAnalyzer",
            _ => "EnableDefaultAnalyzers"
        };

        return _configuration.GetValue<bool>(featureFlagKey, true); // Default: habilitado
    }
}

/// <summary>
/// 🏥 Health Check Function - Dashboard de saúde do sistema
/// </summary>
public class SystemHealthFunction
{
    private readonly ObservabilityService _observability;
    private readonly CircuitBreakerService _circuitBreaker;
    private readonly ILogger<SystemHealthFunction> _logger;

    public SystemHealthFunction(
        ObservabilityService observability,
        CircuitBreakerService circuitBreaker,
        ILogger<SystemHealthFunction> logger)
    {
        _observability = observability;
        _circuitBreaker = circuitBreaker;
        _logger = logger;
    }

    /// <summary>
    /// 🏥 Endpoint para verificar saúde do sistema
    /// </summary>
    [Function("SystemHealth")]
    public async Task<SystemHealthResponse> RunAsync(
        [Microsoft.Azure.Functions.Worker.HttpTrigger(AuthorizationLevel.Function, "get")] 
        Microsoft.Azure.Functions.Worker.Http.HttpRequestData req)
    {
        _logger.LogInformation("🏥 Verificando saúde do sistema...");

        var systemHealth = _observability.GetSystemHealth();
        var circuitBreakerMetrics = _circuitBreaker.GetMetrics();
        var detailedMetrics = _observability.GetDetailedMetrics();

        return new SystemHealthResponse
        {
            IsHealthy = systemHealth.IsHealthy && circuitBreakerMetrics.IsHealthy,
            SystemMetrics = systemHealth,
            CircuitBreakerMetrics = circuitBreakerMetrics,
            DetailedMetrics = detailedMetrics,
            Timestamp = DateTime.UtcNow
        };
    }
}

/// <summary>
/// 🏥 Resposta do health check
/// </summary>
public class SystemHealthResponse
{
    public bool IsHealthy { get; set; }
    public SystemHealthMetrics? SystemMetrics { get; set; }
    public CircuitBreakerMetrics? CircuitBreakerMetrics { get; set; }
    public Dictionary<string, object>? DetailedMetrics { get; set; }
    public DateTime Timestamp { get; set; }
}