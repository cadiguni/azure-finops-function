using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// 🛡️ CIRCUIT BREAKER para Azure Monitor API
/// 
/// Proteção automática contra throttling:
/// - Reduz paralelismo quando detecta 429 errors
/// - Aumenta gradualmente quando volta ao normal  
/// - Logs detalhados para monitoramento
/// </summary>
public class CircuitBreakerService
{
    private readonly ILogger<CircuitBreakerService> _logger;
    
    // 🚦 Estado do Circuit Breaker
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private DateTime _lastFailureTime = DateTime.MinValue;
    private int _failureCount = 0;
    private int _currentParallelism = 5; // Padrão: 5 threads
    
    // 📊 Configurações
    private readonly int _failureThreshold = 3;        // 3 falhas consecutivas
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(2);  // 2 min para retry
    private readonly int _minParallelism = 1;          // Mínimo: 1 thread
    private readonly int _maxParallelism = 10;         // Máximo: 10 threads

    public CircuitBreakerService(ILogger<CircuitBreakerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 📈 Obtém paralelismo atual baseado no estado do circuit breaker
    /// </summary>
    public int GetCurrentParallelism()
    {
        UpdateStateIfNeeded();
        return _currentParallelism;
    }

    /// <summary>
    /// ✅ Registra sucesso - melhora paralelismo gradualmente
    /// </summary>
    public void RecordSuccess()
    {
        if (_state == CircuitBreakerState.HalfOpen)
        {
            _logger.LogInformation("🟢 Circuit Breaker: Sucesso após falha - voltando ao normal");
            _state = CircuitBreakerState.Closed;
            _failureCount = 0;
        }

        // Aumenta paralelismo gradualmente se estiver abaixo do máximo
        if (_currentParallelism < _maxParallelism && _failureCount == 0)
        {
            _currentParallelism = Math.Min(_currentParallelism + 1, _maxParallelism);
            _logger.LogDebug("📈 Aumentando paralelismo para {parallelism}", _currentParallelism);
        }
    }

    /// <summary>
    /// ❌ Registra falha - reduz paralelismo automaticamente
    /// </summary>
    public void RecordFailure(Exception exception)
    {
        _failureCount++;
        _lastFailureTime = DateTime.UtcNow;

        // 🚨 Detecta throttling do Azure Monitor
        var isThrottling = IsThrottlingError(exception);
        
        if (isThrottling)
        {
            _logger.LogWarning("🚨 THROTTLING DETECTADO: {error} - Reduzindo paralelismo", exception.Message);
            _currentParallelism = Math.Max(_currentParallelism / 2, _minParallelism);
        }

        if (_failureCount >= _failureThreshold)
        {
            _state = CircuitBreakerState.Open;
            _logger.LogError("🔴 Circuit Breaker ABERTO após {count} falhas - Paralelismo: {parallelism}", 
                _failureCount, _currentParallelism);
        }
    }

    /// <summary>
    /// 🔍 Detecta se é erro de throttling do Azure
    /// </summary>
    private bool IsThrottlingError(Exception exception)
    {
        var message = exception.Message.ToLower();
        return message.Contains("throttl") || 
               message.Contains("429") || 
               message.Contains("too many requests") ||
               message.Contains("rate limit");
    }

    /// <summary>
    /// 🔄 Atualiza estado baseado no tempo
    /// </summary>
    private void UpdateStateIfNeeded()
    {
        if (_state == CircuitBreakerState.Open && 
            DateTime.UtcNow - _lastFailureTime > _timeout)
        {
            _logger.LogInformation("🟡 Circuit Breaker: Tentando half-open após timeout");
            _state = CircuitBreakerState.HalfOpen;
        }
    }

    /// <summary>
    /// 📊 Obtém métricas atuais do circuit breaker
    /// </summary>
    public CircuitBreakerMetrics GetMetrics()
    {
        return new CircuitBreakerMetrics
        {
            State = _state.ToString(),
            CurrentParallelism = _currentParallelism,
            FailureCount = _failureCount,
            LastFailureTime = _lastFailureTime,
            IsHealthy = _state == CircuitBreakerState.Closed && _failureCount == 0
        };
    }
}

/// <summary>
/// 🚦 Estados do Circuit Breaker
/// </summary>
public enum CircuitBreakerState
{
    Closed,    // Normal - permite todas as chamadas
    Open,      // Falha - bloqueia chamadas temporariamente  
    HalfOpen   // Teste - permite algumas chamadas para testar recovery
}

/// <summary>
/// 📊 Métricas do Circuit Breaker
/// </summary>
public class CircuitBreakerMetrics
{
    public string State { get; set; } = "";
    public int CurrentParallelism { get; set; }
    public int FailureCount { get; set; }
    public DateTime LastFailureTime { get; set; }
    public bool IsHealthy { get; set; }
}
