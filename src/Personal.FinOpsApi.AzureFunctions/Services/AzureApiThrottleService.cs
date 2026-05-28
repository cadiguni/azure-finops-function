using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
///  Throttling service para controlar chamadas simultâneas ao Azure ARM/Monitor
/// Resolve 429 Rate Limiting usando SemaphoreSlim global
/// </summary>
public class AzureApiThrottleService
{
    private readonly SemaphoreSlim _armThrottle;
    private readonly SemaphoreSlim _monitorThrottle;
    private readonly ILogger<AzureApiThrottleService> _logger;

    public AzureApiThrottleService(ILogger<AzureApiThrottleService> logger)
    {
        _logger = logger;
        //  LIMITES CONSERVADORES: ARM=2, Monitor=3 (ajustar conforme necessário)
        _armThrottle = new SemaphoreSlim(2, 2);
        _monitorThrottle = new SemaphoreSlim(3, 3);
    }

    /// <summary>
    ///  Execute ARM API call com throttling automático
    /// </summary>
    public async Task<T> ExecuteArmCallAsync<T>(Func<Task<T>> apiCall, string operationName = "ARM API")
    {
        await _armThrottle.WaitAsync();
        try
        {
            _logger.LogDebug(" [ARM] Executando: {operation} (slots disponíveis: {available})", 
                operationName, _armThrottle.CurrentCount);
                
            var result = await apiCall();
            
            _logger.LogDebug(" [ARM] Concluído: {operation}", operationName);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, " [ARM] Falhou: {operation} - {error}", operationName, ex.Message);
            throw;
        }
        finally
        {
            _armThrottle.Release();
        }
    }

    /// <summary>
    ///  Execute Monitor API call com throttling automático
    /// </summary>
    public async Task<T> ExecuteMonitorCallAsync<T>(Func<Task<T>> apiCall, string operationName = "Monitor API")
    {
        await _monitorThrottle.WaitAsync();
        try
        {
            _logger.LogDebug(" [Monitor] Executando: {operation} (slots disponíveis: {available})", 
                operationName, _monitorThrottle.CurrentCount);
                
            var result = await apiCall();
            
            _logger.LogDebug(" [Monitor] Concluído: {operation}", operationName);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, " [Monitor] Falhou: {operation} - {error}", operationName, ex.Message);
            throw;
        }
        finally
        {
            _monitorThrottle.Release();
        }
    }

    /// <summary>
    ///  Execute multiple operations sequentially (evita thundering herd)
    /// </summary>
    public async Task<T[]> ExecuteSequentiallyAsync<T>(IEnumerable<Func<Task<T>>> operations, string batchName = "Sequential batch")
    {
        _logger.LogInformation(" Executando {count} operações sequencialmente: {batch}", 
            operations.Count(), batchName);
            
        var results = new List<T>();
        var operationArray = operations.ToArray();
        
        for (int i = 0; i < operationArray.Length; i++)
        {
            try
            {
                _logger.LogDebug(" [{current}/{total}] Executando operação {batch}", 
                    i + 1, operationArray.Length, batchName);
                    
                var result = await operationArray[i]();
                results.Add(result);
                
                // Small delay between operations to be extra nice to APIs
                if (i < operationArray.Length - 1)
                {
                    await Task.Delay(100);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, " [{current}/{total}] Operação falhou em {batch}: {error}", 
                    i + 1, operationArray.Length, batchName, ex.Message);
                throw;
            }
        }
        
        _logger.LogInformation(" Completadas {count} operações sequenciais: {batch}", 
            results.Count, batchName);
            
        return results.ToArray();
    }

    /// <summary>
    ///  Estatísticas de uso dos throttles
    /// </summary>
    public (int ArmAvailable, int MonitorAvailable) GetAvailableSlots()
    {
        return (_armThrottle.CurrentCount, _monitorThrottle.CurrentCount);
    }

    public void Dispose()
    {
        _armThrottle?.Dispose();
        _monitorThrottle?.Dispose();
    }
}