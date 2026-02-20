using System.Collections.Concurrent;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// 🚦 Classe para limitar concorrência e evitar 429 "Too Many Requests"
/// Substitui Task.WhenAll por execução throttled com SemaphoreSlim
/// </summary>
public static class Throttle
{
    /// <summary>
    /// Executa tasks com resultado limitando concorrência
    /// </summary>
    public static async Task<List<T>> WhenAllThrottled<T>(
        IEnumerable<Func<Task<T>>> factories,
        int maxConcurrency,
        CancellationToken ct = default)
    {
        using var sem = new SemaphoreSlim(maxConcurrency);
        var tasks = factories.Select(async f =>
        {
            await sem.WaitAsync(ct);
            try 
            { 
                return await f(); 
            }
            finally 
            { 
                sem.Release(); 
            }
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    /// <summary>
    /// Executa tasks sem resultado limitando concorrência
    /// </summary>
    public static async Task WhenAllThrottled(
        IEnumerable<Func<Task>> factories,
        int maxConcurrency,
        CancellationToken ct = default)
    {
        using var sem = new SemaphoreSlim(maxConcurrency);
        var tasks = factories.Select(async f =>
        {
            await sem.WaitAsync(ct);
            try 
            { 
                await f(); 
            }
            finally 
            { 
                sem.Release(); 
            }
        });

        await Task.WhenAll(tasks);
    }
}