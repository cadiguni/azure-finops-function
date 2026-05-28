using Microsoft.Extensions.Logging;
using System.Net;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
///  Serviço para retry resiliente de APIs do Azure com tratamento de 429 Rate Limiting
/// </summary>
public class HttpRetryService
{
    private readonly ILogger<HttpRetryService> _logger;
    private static readonly Random _random = new Random();

    public HttpRetryService(ILogger<HttpRetryService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///  Executa HTTP request com retry automático e tratamento de 429
    /// </summary>
    public async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient httpClient, 
        HttpRequestMessage request, 
        CancellationToken cancellationToken = default,
        int maxAttempts = 6)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // Clone request para retry (necessário pois HttpRequestMessage é single-use)
                var clonedRequest = await CloneHttpRequestMessageAsync(request);
                
                var response = await httpClient.SendAsync(clonedRequest, cancellationToken);

                //  Se não é 429, retorna direto
                if (response.StatusCode != (HttpStatusCode)429)
                {
                    if (attempt > 1)
                    {
                        _logger.LogInformation(" Request succeeded on attempt {attempt}", attempt);
                    }
                    return response;
                }

                //  Rate Limited - calcular delay
                var delay = CalculateRetryDelay(response, attempt);
                
                _logger.LogWarning(" Rate limited (429) - attempt {attempt}/{max}. Retrying in {delay}ms", 
                    attempt, maxAttempts, delay.TotalMilliseconds);

                // Se é a última tentativa, lança RateLimitedException
                if (attempt == maxAttempts)
                {
                    _logger.LogError(" Rate limit persistiu após {attempts} tentativas", maxAttempts);
                    throw new RateLimitedException($"429 Too Many Requests persistente após {maxAttempts} tentativas para {request.RequestUri}");
                }

                await Task.Delay(delay, cancellationToken);
                response.Dispose(); // Libera recursos
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                lastException = ex;
                _logger.LogWarning(ex, " Request failed on attempt {attempt}/{max}: {error}", 
                    attempt, maxAttempts, ex.Message);

                if (attempt == maxAttempts)
                {
                    throw;
                }

                // Delay menor para exceptions não-HTTP
                var delay = TimeSpan.FromSeconds(Math.Min(5, Math.Pow(1.5, attempt)));
                await Task.Delay(delay, cancellationToken);
            }
        }

        // Não deveria chegar aqui, mas just in case
        throw lastException ?? new InvalidOperationException("Retry logic error");
    }

    /// <summary>
    ///  Calcula delay de retry respeitando Retry-After header
    /// </summary>
    private TimeSpan CalculateRetryDelay(HttpResponseMessage response, int attempt)
    {
        // 1⃣ Tentar usar Retry-After header se presente
        if (response.Headers.RetryAfter?.Delta is TimeSpan retryAfter)
        {
            // Limitar a 5 minutos por segurança
            var cappedRetryAfter = retryAfter > TimeSpan.FromMinutes(5) 
                ? TimeSpan.FromMinutes(5) 
                : retryAfter;

            _logger.LogDebug(" Using Retry-After header: {delay}s", cappedRetryAfter.TotalSeconds);
            return AddJitter(cappedRetryAfter);
        }

        // 2⃣ Fallback para exponential backoff
        var baseDelay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
        return AddJitter(baseDelay);
    }

    /// <summary>
    ///  Adiciona jitter para evitar thundering herd
    /// </summary>
    private static TimeSpan AddJitter(TimeSpan baseDelay)
    {
        var jitterMs = _random.Next(0, 1000); // 0-1s de jitter
        return baseDelay.Add(TimeSpan.FromMilliseconds(jitterMs));
    }

    /// <summary>
    ///  Clona HttpRequestMessage para retry (necessário pois é single-use)
    /// </summary>
    private static async Task<HttpRequestMessage> CloneHttpRequestMessageAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);

        // Copy headers
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Copy content if present
        if (original.Content != null)
        {
            var contentBytes = await original.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(contentBytes);

            // Copy content headers
            foreach (var header in original.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    /// <summary>
    ///  Wrapper para chamadas GET com retry automático
    /// </summary>
    public async Task<HttpResponseMessage> GetWithRetryAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendWithRetryAsync(httpClient, request, cancellationToken);
    }

    /// <summary>
    ///  Wrapper para chamadas POST com retry automático
    /// </summary>
    public async Task<HttpResponseMessage> PostWithRetryAsync(
        HttpClient httpClient,
        string url,
        HttpContent content,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        return await SendWithRetryAsync(httpClient, request, cancellationToken);
    }
}