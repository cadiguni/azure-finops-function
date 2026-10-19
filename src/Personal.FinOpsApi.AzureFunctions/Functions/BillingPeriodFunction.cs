using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class BillingPeriodFunction
{
    private readonly IBillingPeriodResolver _billingPeriodResolver;
    private readonly ICurrentUserService _currentUserService;
    private readonly AuthorizationService _authorizationService;
    private readonly ILogger<BillingPeriodFunction> _logger;

    public BillingPeriodFunction(
        IBillingPeriodResolver billingPeriodResolver,
        ICurrentUserService currentUserService,
        AuthorizationService authorizationService,
        ILogger<BillingPeriodFunction> logger)
    {
        _billingPeriodResolver = billingPeriodResolver;
        _currentUserService = currentUserService;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    [Function("BillingPeriodOverridesGet")]
    public async Task<HttpResponseData> ListOverrides(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "billing/periods/overrides")] HttpRequestData req)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanEdit(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão administrativa.");
        }

        try
        {
            var overrides = await _billingPeriodResolver.ListOverridesAsync();
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                count = overrides.Count,
                overrides = overrides.Select(ovr => new
                {
                    subscriptionId = ovr.SubscriptionId,
                    billingCycleStartDay = ovr.BillingCycleStartDay,
                    updatedAt = ovr.UpdatedAt,
                    updatedBy = ovr.UpdatedBy
                })
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BILLING-PERIOD] Erro ao listar overrides de billing period");
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao listar overrides de billing period.");
        }
    }

    [Function("BillingPeriodOverrideUpsert")]
    public async Task<HttpResponseData> UpsertOverride(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "billing/periods/overrides/{subscriptionId}")] HttpRequestData req,
        string subscriptionId)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanEdit(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão administrativa.");
        }

        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", "subscriptionId é obrigatório.");
        }

        var payload = await ReadUpsertPayloadAsync(req);
        if (payload is null)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", "Payload inválido.");
        }

        if (payload.BillingCycleStartDay is null || payload.BillingCycleStartDay < 1 || payload.BillingCycleStartDay > 28)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", "billingCycleStartDay deve estar entre 1 e 28.");
        }

        try
        {
            var updated = await _billingPeriodResolver.UpsertOverrideAsync(
                subscriptionId,
                payload.BillingCycleStartDay.Value,
                ResolveUpdatedBy(user));

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                subscriptionId = updated.SubscriptionId,
                billingCycleStartDay = updated.BillingCycleStartDay,
                updatedAt = updated.UpdatedAt,
                updatedBy = updated.UpdatedBy
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BILLING-PERIOD] Erro ao salvar override para {subscriptionId}", subscriptionId);
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao salvar override de billing period.");
        }
    }

    [Function("BillingPeriodOverrideDelete")]
    public async Task<HttpResponseData> DeleteOverride(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "billing/periods/overrides/{subscriptionId}")] HttpRequestData req,
        string subscriptionId)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanEdit(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão administrativa.");
        }

        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", "subscriptionId é obrigatório.");
        }

        try
        {
            var deleted = await _billingPeriodResolver.DeleteOverrideAsync(subscriptionId, ResolveUpdatedBy(user));
            if (!deleted)
            {
                return await ApiHttpResponses.NotFound(req, $"Override de billing period não encontrado para subscription '{subscriptionId}'.");
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                message = "Override removido com sucesso.",
                subscriptionId = subscriptionId.Trim()
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BILLING-PERIOD] Erro ao remover override para {subscriptionId}", subscriptionId);
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao remover override de billing period.");
        }
    }

    private static async Task<BillingPeriodOverrideUpsertRequest?> ReadUpsertPayloadAsync(HttpRequestData req)
    {
        var body = await req.ReadAsStringAsync();
        return JsonSerializer.Deserialize<BillingPeriodOverrideUpsertRequest>(
            body ?? "{}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static string ResolveUpdatedBy(CurrentUserContext user)
    {
        if (!string.IsNullOrWhiteSpace(user.UserPrincipalName))
        {
            return user.UserPrincipalName;
        }

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            return user.DisplayName;
        }

        return "unknown";
    }
}

public sealed class BillingPeriodOverrideUpsertRequest
{
    public int? BillingCycleStartDay { get; set; }
}
