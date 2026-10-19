using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>Bounded per-subscription data health and monthly budget APIs.</summary>
public sealed class GovernanceFunction(
    GovernanceDataHealthService health,
    GovernanceDataHealthOverviewService healthOverview,
    GovernanceBudgetService budgets,
    ICurrentUserService currentUser,
    AuthorizationService authorization,
    ILogger<GovernanceFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict, PropertyNameCaseInsensitive = false
    };
    private const int MaxBodyBytes = 8192;

    [Function("GovernanceDataHealth")]
    public Task<HttpResponseData> DataHealth(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "governance/data-health")] HttpRequestData req) =>
        ExecuteAsync(req, false, async _ =>
        {
            var now = DateTimeOffset.UtcNow;
            var to = ParseDate(req.Query["to"], DateOnly.FromDateTime(now.UtcDateTime).AddDays(-health.DelayDays));
            var from = ParseDate(req.Query["from"], to.AddDays(-13));
            return await JsonAsync(req, await health.GetAsync(req.Query["subscriptionId"] ?? "", from, to, now,
                req.FunctionContext.CancellationToken));
        });

    // Fans out to every known subscription, so it is restricted to admins.
    [Function("GovernanceDataHealthOverview")]
    public Task<HttpResponseData> DataHealthOverview(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "governance/data-health/overview")] HttpRequestData req) =>
        ExecuteAsync(req, false, async _ => await JsonAsync(req,
            await healthOverview.GetAsync(DateTimeOffset.UtcNow, req.FunctionContext.CancellationToken)), adminOnly: true);

    [Function("GovernanceBudgetsList")]
    public Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "governance/budgets")] HttpRequestData req) =>
        ExecuteAsync(req, false, async _ => await JsonAsync(req,
            await budgets.ListAsync(req.FunctionContext.CancellationToken)));

    [Function("GovernanceBudgetEvaluate")]
    public Task<HttpResponseData> Evaluate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "governance/budgets/{id}")] HttpRequestData req, string id) =>
        ExecuteAsync(req, false, async _ =>
        {
            var result = await budgets.EvaluateAsync(id, DateTimeOffset.UtcNow, req.FunctionContext.CancellationToken);
            return result is null ? await ApiHttpResponses.NotFound(req, "Orçamento não encontrado.") : await JsonAsync(req, result);
        });

    [Function("GovernanceBudgetSave")]
    public Task<HttpResponseData> Save(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "governance/budgets/{id}")] HttpRequestData req, string id) =>
        ExecuteAsync(req, true, async user =>
        {
            var input = await ReadBudgetAsync(req);
            var result = await budgets.SaveAsync(id, input, user.UserId, DateTimeOffset.UtcNow,
                req.FunctionContext.CancellationToken);
            return await JsonAsync(req, result, input.Version is null ? HttpStatusCode.Created : HttpStatusCode.OK);
        });

    [Function("GovernanceBudgetDelete")]
    public Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "governance/budgets/{id}")] HttpRequestData req, string id) =>
        ExecuteAsync(req, true, async user =>
        {
            // Query token allows reuse of the portal's existing authenticated sendJson transport.
            var version = req.Query["version"] ?? throw new ArgumentException("Versão obrigatória para excluir.");
            await budgets.DeleteAsync(id, version, user.UserId, DateTimeOffset.UtcNow, req.FunctionContext.CancellationToken);
            return await JsonAsync(req, new { deleted = true });
        });

    private async Task<HttpResponseData> ExecuteAsync(HttpRequestData req, bool write,
        Func<CurrentUserContext, Task<HttpResponseData>> action, bool adminOnly = false)
    {
        var response = await ExecuteCoreAsync(req, write, action, adminOnly);
        response.Headers.Add("Cache-Control", "no-store");
        return response;
    }

    private async Task<HttpResponseData> ExecuteCoreAsync(HttpRequestData req, bool write,
        Func<CurrentUserContext, Task<HttpResponseData>> action, bool adminOnly)
    {
        var user = await currentUser.GetCurrentUserAsync(req, req.FunctionContext.CancellationToken);
        if (!user.IsAuthenticated) return await ApiHttpResponses.Unauthorized(req);
        if (write || adminOnly ? !authorization.CanEdit(user) : !authorization.CanRead(user))
            return await ApiHttpResponses.Forbidden(req);
        if (write && string.IsNullOrWhiteSpace(user.UserId))
            return await ApiHttpResponses.Forbidden(req);
        try { return await action(user); }
        catch (GovernanceBodyTooLargeException)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.RequestEntityTooLarge, "PayloadTooLarge",
                "Corpo JSON excede 8192 bytes.");
        }
        catch (GovernanceAuditIncompleteException ex)
        {
            return await JsonAsync(req, new { code = "AuditIncomplete", message = ex.Message,
                operationId = ex.OperationId, applied = ex.Applied }, HttpStatusCode.ServiceUnavailable);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest",
                ex is JsonException ? "JSON inválido." : ex.Message);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.Conflict, "Conflict",
                "Orçamento alterado por outra sessão. Recarregue antes de salvar/excluir.");
        }
        catch (RequestFailedException ex) when (ex.Status == 404 && ex.ErrorCode == "BlobNotFound")
        {
            return await ApiHttpResponses.NotFound(req, "Orçamento não encontrado.");
        }
        catch (OperationCanceledException) when (req.FunctionContext.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha na API de governança");
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.ServiceUnavailable, "Unavailable",
                "Dados/configuração de governança indisponíveis. Nenhum sucesso foi presumido.");
        }
    }

    private static async Task<GovernanceBudgetInput> ReadBudgetAsync(HttpRequestData req)
    {
        // Read one byte past the limit even without Content-Length or with a dishonest/chunked body.
        var buffer = new byte[MaxBodyBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await req.Body.ReadAsync(buffer.AsMemory(length), req.FunctionContext.CancellationToken);
            if (read == 0) break;
            length += read;
        }
        if (length > MaxBodyBytes) throw new GovernanceBodyTooLargeException();
        using var json = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8 });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in json.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw new JsonException("Propriedade duplicada.");
        return json.RootElement.Deserialize<GovernanceBudgetInput>(JsonOptions) ?? throw new JsonException();
    }

    private sealed class GovernanceBodyTooLargeException : Exception { }

    private static DateOnly ParseDate(string? value, DateOnly fallback) => value is null ? fallback
        : DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : throw new ArgumentException("Data inválida; informe yyyy-MM-dd.");

    private static async Task<HttpResponseData> JsonAsync(HttpRequestData req, object value, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(value, JsonOptions));
        return response;
    }
}