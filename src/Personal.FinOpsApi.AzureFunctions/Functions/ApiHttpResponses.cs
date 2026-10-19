using Microsoft.Azure.Functions.Worker.Http;
using System.Net;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

internal static class ApiHttpResponses
{
    public static Task<HttpResponseData> Unauthorized(HttpRequestData req, string message = "Usuário não autenticado.")
        => WriteErrorAsync(req, HttpStatusCode.Unauthorized, "Unauthorized", message);

    public static Task<HttpResponseData> Forbidden(HttpRequestData req, string message = "Acesso negado.")
        => WriteErrorAsync(req, HttpStatusCode.Forbidden, "Forbidden", message);

    public static Task<HttpResponseData> NotFound(HttpRequestData req, string message)
        => WriteErrorAsync(req, HttpStatusCode.NotFound, "NotFound", message);

    public static Task<HttpResponseData> InternalServerError(HttpRequestData req, string message = "Erro interno ao processar a requisição.")
        => WriteErrorAsync(req, HttpStatusCode.InternalServerError, "InternalServerError", message);

    public static async Task<HttpResponseData> WriteErrorAsync(
        HttpRequestData req,
        HttpStatusCode statusCode,
        string error,
        string message,
        object? details = null)
    {
        var response = req.CreateResponse(statusCode);
        if (details == null)
        {
            await response.WriteAsJsonAsync(new { error, message });
            return response;
        }

        await response.WriteAsJsonAsync(new { error, message, details });
        return response;
    }
}
