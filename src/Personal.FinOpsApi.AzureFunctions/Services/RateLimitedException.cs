namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
///  Exception para rate limiting (429 Too Many Requests)
/// Usado para identificar quando reagendar mensagem em vez de falhar
/// </summary>
public class RateLimitedException : Exception
{
    public RateLimitedException(string message) : base(message) { }
    public RateLimitedException(string message, Exception innerException) : base(message, innerException) { }
}