using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// 📊 OBSERVABILIDADE ENTERPRISE - Métricas de negócio FinOps
/// 
/// Coleta métricas essenciais:
/// - TotalSavingsFound
/// - AnalyzerExecutionTime  
/// - SubscriptionsProcessed
/// - AzureMonitorCalls
/// - ErrorRates
/// 
/// Isso vira dashboard de saúde do sistema 📈
/// </summary>
public class ObservabilityService
{
    private readonly ILogger<ObservabilityService> _logger;
    
    // 📊 Métricas em memória (em produção usar Application Insights Custom Metrics)
    private readonly Dictionary<string, double> _metrics = new();
    private readonly Dictionary<string, int> _counters = new();
    private readonly List<AnalyzerExecutionMetric> _executionHistory = new();

    public ObservabilityService(ILogger<ObservabilityService> logger)
    {
        _logger = logger;
        InitializeMetrics();
    }

    /// <summary>
    /// 💰 Registra economia encontrada por analyzer
    /// </summary>
    public void RecordSavingsFound(string analyzerType, decimal savingsAmount, string currency = "BRL")
    {
        var metricKey = $"TotalSavingsFound_{analyzerType}";
        _metrics[metricKey] = _metrics.GetValueOrDefault(metricKey, 0) + (double)savingsAmount;
        _metrics["TotalSavingsFound"] = _metrics.GetValueOrDefault("TotalSavingsFound", 0) + (double)savingsAmount;

        _logger.LogInformation("💰 {analyzer}: R$ {savings:F2} em economias encontradas", 
            analyzerType, savingsAmount);
    }

    /// <summary>
    /// ⏱️ Registra tempo de execução de analyzer
    /// </summary>
    public void RecordAnalyzerExecutionTime(string analyzerType, TimeSpan executionTime, bool success = true)
    {
        var execution = new AnalyzerExecutionMetric
        {
            AnalyzerType = analyzerType,
            ExecutionTime = executionTime,
            Success = success,
            Timestamp = DateTime.UtcNow
        };

        _executionHistory.Add(execution);

        // Manter apenas últimas 100 execuções em memória
        if (_executionHistory.Count > 100)
        {
            _executionHistory.RemoveAt(0);
        }

        var metricKey = $"AnalyzerExecutionTime_{analyzerType}";
        _metrics[metricKey] = executionTime.TotalMilliseconds;

        _logger.LogInformation("⏱️ {analyzer} executado em {duration}ms - {status}", 
            analyzerType, executionTime.TotalMilliseconds, success ? "SUCCESS" : "FAILED");
    }

    /// <summary>
    /// 📋 Registra subscription processada
    /// </summary>
    public void RecordSubscriptionProcessed(string subscriptionId, string analyzerType, int findingsCount)
    {
        var counterKey = "SubscriptionsProcessed";
        _counters[counterKey] = _counters.GetValueOrDefault(counterKey, 0) + 1;
        
        var analyzerCounterKey = $"SubscriptionsProcessed_{analyzerType}";
        _counters[analyzerCounterKey] = _counters.GetValueOrDefault(analyzerCounterKey, 0) + 1;

        _logger.LogDebug("📋 Subscription {sub} processada por {analyzer}: {findings} findings", 
            subscriptionId, analyzerType, findingsCount);
    }

    /// <summary>
    /// 🔵 Registra chamada para Azure Monitor API
    /// </summary>
    public void RecordAzureMonitorCall(string metricType, bool success = true, TimeSpan? duration = null)
    {
        var counterKey = "AzureMonitorCalls";
        _counters[counterKey] = _counters.GetValueOrDefault(counterKey, 0) + 1;

        if (!success)
        {
            var errorKey = "AzureMonitorErrors";
            _counters[errorKey] = _counters.GetValueOrDefault(errorKey, 0) + 1;
        }

        if (duration.HasValue)
        {
            var durationKey = $"AzureMonitorDuration_{metricType}";
            _metrics[durationKey] = duration.Value.TotalMilliseconds;
        }

        _logger.LogDebug("🔵 Azure Monitor call: {metric} - {status} ({duration}ms)", 
            metricType, success ? "SUCCESS" : "FAILED", duration?.TotalMilliseconds ?? 0);
    }

    /// <summary>
    /// 🎯 Registra erro geral do sistema
    /// </summary>
    public void RecordError(string component, Exception exception)
    {
        var errorKey = $"Errors_{component}";
        _counters[errorKey] = _counters.GetValueOrDefault(errorKey, 0) + 1;
        
        _counters["TotalErrors"] = _counters.GetValueOrDefault("TotalErrors", 0) + 1;

        _logger.LogError(exception, "❌ Erro em {component}: {error}", component, exception.Message);
    }

    /// <summary>
    /// 📈 Obtém métricas consolidadas para dashboard
    /// </summary>
    public SystemHealthMetrics GetSystemHealth()
    {
        var totalSubscriptions = _counters.GetValueOrDefault("SubscriptionsProcessed", 0);
        var totalErrors = _counters.GetValueOrDefault("TotalErrors", 0);
        var azureMonitorCalls = _counters.GetValueOrDefault("AzureMonitorCalls", 0);
        var azureMonitorErrors = _counters.GetValueOrDefault("AzureMonitorErrors", 0);

        var avgExecutionTime = _executionHistory.Any() ? 
            _executionHistory.Average(e => e.ExecutionTime.TotalMilliseconds) : 0;

        return new SystemHealthMetrics
        {
            TotalSavingsFound = _metrics.GetValueOrDefault("TotalSavingsFound", 0),
            SubscriptionsProcessed = totalSubscriptions,
            TotalErrors = totalErrors,
            ErrorRate = totalSubscriptions > 0 ? (double)totalErrors / totalSubscriptions * 100 : 0,
            AzureMonitorCalls = azureMonitorCalls,
            AzureMonitorErrorRate = azureMonitorCalls > 0 ? (double)azureMonitorErrors / azureMonitorCalls * 100 : 0,
            AvgExecutionTimeMs = avgExecutionTime,
            LastUpdated = DateTime.UtcNow,
            IsHealthy = totalErrors < 5 && (azureMonitorCalls == 0 || azureMonitorErrors < azureMonitorCalls * 0.1)
        };
    }

    /// <summary>
    /// 📊 Obtém métricas detalhadas por analyzer
    /// </summary>
    public Dictionary<string, object> GetDetailedMetrics()
    {
        var detailed = new Dictionary<string, object>();

        // Métricas por analyzer
        var analyzerTypes = new[] { "Storage", "VM", "AppService", "PublicIP", "Disk" };
        
        foreach (var analyzer in analyzerTypes)
        {
            detailed[$"Savings_{analyzer}"] = _metrics.GetValueOrDefault($"TotalSavingsFound_{analyzer}", 0);
            detailed[$"Subscriptions_{analyzer}"] = _counters.GetValueOrDefault($"SubscriptionsProcessed_{analyzer}", 0);
            detailed[$"ExecutionTime_{analyzer}"] = _metrics.GetValueOrDefault($"AnalyzerExecutionTime_{analyzer}", 0);
        }

        // Métricas gerais
        detailed["TotalSavings"] = _metrics.GetValueOrDefault("TotalSavingsFound", 0);
        detailed["TotalSubscriptions"] = _counters.GetValueOrDefault("SubscriptionsProcessed", 0);
        detailed["TotalAzureMonitorCalls"] = _counters.GetValueOrDefault("AzureMonitorCalls", 0);
        detailed["TotalErrors"] = _counters.GetValueOrDefault("TotalErrors", 0);

        return detailed;
    }

    /// <summary>
    /// 🔄 Inicializa métricas zeradas
    /// </summary>
    private void InitializeMetrics()
    {
        _metrics["TotalSavingsFound"] = 0;
        _counters["SubscriptionsProcessed"] = 0;
        _counters["AzureMonitorCalls"] = 0;
        _counters["TotalErrors"] = 0;
    }

    /// <summary>
    /// 🧹 Reset métricas (útil para testes)
    /// </summary>
    public void ResetMetrics()
    {
        _metrics.Clear();
        _counters.Clear();
        _executionHistory.Clear();
        InitializeMetrics();
        
        _logger.LogInformation("🧹 Métricas resetadas");
    }
}

/// <summary>
/// ⏱️ Métrica de execução de analyzer individual
/// </summary>
public class AnalyzerExecutionMetric
{
    public string AnalyzerType { get; set; } = "";
    public TimeSpan ExecutionTime { get; set; }
    public bool Success { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// 💊 Métricas de saúde do sistema
/// </summary>
public class SystemHealthMetrics
{
    public double TotalSavingsFound { get; set; }
    public int SubscriptionsProcessed { get; set; }
    public int TotalErrors { get; set; }
    public double ErrorRate { get; set; }
    public int AzureMonitorCalls { get; set; }
    public double AzureMonitorErrorRate { get; set; }
    public double AvgExecutionTimeMs { get; set; }
    public DateTime LastUpdated { get; set; }
    public bool IsHealthy { get; set; }
}
