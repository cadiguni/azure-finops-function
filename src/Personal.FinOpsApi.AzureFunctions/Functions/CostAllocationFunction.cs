using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// Rateio por centro de custo: leitura para quem tem FinOps.Reader, ajustes manuais para
/// FinOps.Admin. As tags do ambiente estão defasadas, então a régua de atribuição é editável —
/// e cada ajuste fica identificado como manual na resposta.
/// </summary>
public sealed class CostAllocationFunction
{
    /// <summary>Cada dia do período custa uma leitura de blob por assinatura.</summary>
    private const int MaximumRangeDays = 62;

    private readonly CostAllocationService _allocationService;
    private readonly CostAllocationRulesStorageService _rulesStorage;
    private readonly IConfiguration _configuration;
    private readonly ICurrentUserService _currentUserService;
    private readonly AuthorizationService _authorizationService;
    private readonly ILogger<CostAllocationFunction> _logger;

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CostAllocationFunction(
        CostAllocationService allocationService,
        CostAllocationRulesStorageService rulesStorage,
        IConfiguration configuration,
        ICurrentUserService currentUserService,
        AuthorizationService authorizationService,
        ILogger<CostAllocationFunction> logger)
    {
        _allocationService = allocationService;
        _rulesStorage = rulesStorage;
        _configuration = configuration;
        _currentUserService = currentUserService;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    [Function("CostAllocationBreakdown")]
    public async Task<HttpResponseData> GetAllocationAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost/allocation")] HttpRequestData req)
    {
        var denied = await RequireReaderAsync(req);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            var range = ResolveRange(req);
            if (range.Error is not null)
            {
                return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", range.Error);
            }

            var subscription = ResolveSubscriptionScope(req);
            var breakdown = await _allocationService.BuildAsync(range.From, range.To, subscription);
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(breakdown);
            return response;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Erro no endpoint CostAllocationBreakdown");
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao montar o rateio por centro de custo.");
        }
    }

    [Function("CostAllocationListRules")]
    public async Task<HttpResponseData> ListRulesAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost/allocation/rules")] HttpRequestData req)
    {
        var denied = await RequireReaderAsync(req);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            var config = await _rulesStorage.GetConfigAsync();
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(config);
            return response;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Erro no endpoint CostAllocationListRules");
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao listar as regras de rateio.");
        }
    }

    [Function("CostAllocationCreateRule")]
    public async Task<HttpResponseData> CreateRuleAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "cost/allocation/rules")] HttpRequestData req)
    {
        var (user, denied) = await RequireAdminAsync(req);
        if (denied is not null)
        {
            return denied;
        }

        var payload = await ReadPayloadAsync(req);
        if (payload is null)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", "Payload inválido.");
        }

        var validationError = Validate(payload);
        if (validationError is not null)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", validationError);
        }

        var config = await _rulesStorage.GetConfigAsync();
        var duplicate = config.Rules.FirstOrDefault(rule => IsSameTarget(rule, payload));
        if (duplicate is not null)
        {
            return await ApiHttpResponses.WriteErrorAsync(
                req,
                HttpStatusCode.Conflict,
                "Conflict",
                $"Já existe uma regra '{duplicate.Type}' para '{duplicate.Match}' nessa assinatura.");
        }

        var updatedBy = ResolveUpdatedBy(user);
        var created = new CostAllocationRule
        {
            Id = Guid.NewGuid().ToString("n"),
            Type = CostAllocationRuleTypes.Normalize(payload.Type)!,
            Match = payload.Match!.Trim(),
            CostCenter = payload.CostCenter!.Trim(),
            SubscriptionId = payload.SubscriptionId?.Trim() ?? string.Empty,
            Enabled = payload.Enabled ?? true,
            Note = payload.Note?.Trim() ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = updatedBy,
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = updatedBy
        };

        config.Rules.Add(created);
        await _rulesStorage.SaveConfigAsync(config, updatedBy);

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(created);
        return response;
    }

    [Function("CostAllocationUpdateRule")]
    public async Task<HttpResponseData> UpdateRuleAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "cost/allocation/rules/{ruleId}")] HttpRequestData req,
        string ruleId)
    {
        var (user, denied) = await RequireAdminAsync(req);
        if (denied is not null)
        {
            return denied;
        }

        var payload = await ReadPayloadAsync(req);
        if (payload is null)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", "Payload inválido.");
        }

        var config = await _rulesStorage.GetConfigAsync();
        var rule = config.Rules.FirstOrDefault(item => item.Id.Equals(ruleId, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
        {
            return await ApiHttpResponses.NotFound(req, $"Regra '{ruleId}' não encontrada.");
        }

        // Campos ausentes preservam o valor atual: o portal manda só o que mudou.
        var merged = new CostAllocationRulePayload
        {
            Type = payload.Type ?? rule.Type,
            Match = payload.Match ?? rule.Match,
            CostCenter = payload.CostCenter ?? rule.CostCenter,
            SubscriptionId = payload.SubscriptionId ?? rule.SubscriptionId,
            Enabled = payload.Enabled ?? rule.Enabled,
            Note = payload.Note ?? rule.Note
        };

        var validationError = Validate(merged);
        if (validationError is not null)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", validationError);
        }

        rule.Type = CostAllocationRuleTypes.Normalize(merged.Type)!;
        rule.Match = merged.Match!.Trim();
        rule.CostCenter = merged.CostCenter!.Trim();
        rule.SubscriptionId = merged.SubscriptionId?.Trim() ?? string.Empty;
        rule.Enabled = merged.Enabled ?? true;
        rule.Note = merged.Note?.Trim() ?? string.Empty;
        rule.UpdatedAt = DateTime.UtcNow;
        rule.UpdatedBy = ResolveUpdatedBy(user);

        await _rulesStorage.SaveConfigAsync(config, rule.UpdatedBy);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(rule);
        return response;
    }

    [Function("CostAllocationDeleteRule")]
    public async Task<HttpResponseData> DeleteRuleAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "cost/allocation/rules/{ruleId}")] HttpRequestData req,
        string ruleId)
    {
        var (user, denied) = await RequireAdminAsync(req);
        if (denied is not null)
        {
            return denied;
        }

        var config = await _rulesStorage.GetConfigAsync();
        var rule = config.Rules.FirstOrDefault(item => item.Id.Equals(ruleId, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
        {
            return await ApiHttpResponses.NotFound(req, $"Regra '{ruleId}' não encontrada.");
        }

        config.Rules.Remove(rule);
        await _rulesStorage.SaveConfigAsync(config, ResolveUpdatedBy(user));

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Regra removida.", ruleId = rule.Id });
        return response;
    }

    private static string? Validate(CostAllocationRulePayload payload)
    {
        if (!CostAllocationRuleTypes.IsKnown(payload.Type))
        {
            return $"Tipo de regra inválido. Use um destes: {string.Join(", ", CostAllocationRuleTypes.All)}.";
        }

        if (string.IsNullOrWhiteSpace(payload.Match))
        {
            return "Informe o valor a ser comparado (resource group, prefixo, serviço ou valor da tag).";
        }

        return string.IsNullOrWhiteSpace(payload.CostCenter)
            ? "Informe o centro de custo que a regra atribui."
            : null;
    }

    private static bool IsSameTarget(CostAllocationRule rule, CostAllocationRulePayload payload) =>
        rule.Type.Equals(CostAllocationRuleTypes.Normalize(payload.Type), StringComparison.OrdinalIgnoreCase)
        && rule.Match.Equals(payload.Match?.Trim(), StringComparison.OrdinalIgnoreCase)
        && (rule.SubscriptionId ?? string.Empty)
            .Equals(payload.SubscriptionId?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static async Task<CostAllocationRulePayload?> ReadPayloadAsync(HttpRequestData req)
    {
        try
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            return string.IsNullOrWhiteSpace(body)
                ? null
                : JsonSerializer.Deserialize<CostAllocationRulePayload>(body, PayloadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<HttpResponseData?> RequireReaderAsync(HttpRequestData req)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        return _authorizationService.CanRead(user)
            ? null
            : await ApiHttpResponses.Forbidden(req, "Usuário sem permissão de leitura FinOps.");
    }

    private async Task<(CurrentUserContext User, HttpResponseData? Denied)> RequireAdminAsync(HttpRequestData req)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return (user, await ApiHttpResponses.Unauthorized(req));
        }

        return _authorizationService.CanEdit(user)
            ? (user, null)
            : (user, await ApiHttpResponses.Forbidden(req, "Usuário sem permissão para alterar o rateio."));
    }

    private static string ResolveUpdatedBy(CurrentUserContext user) =>
        !string.IsNullOrWhiteSpace(user.UserPrincipalName) ? user.UserPrincipalName
        : !string.IsNullOrWhiteSpace(user.DisplayName) ? user.DisplayName
        : "unknown";

    /// <summary>
    /// Sem parâmetro, o escopo é produção. "all" abre para todas as assinaturas, de forma
    /// explícita — quem quiser o consolidado pede por ele.
    /// </summary>
    private string? ResolveSubscriptionScope(HttpRequestData req)
    {
        var requested = req.Query?["subscription"]?.Trim();
        if (string.IsNullOrWhiteSpace(requested))
        {
            return _allocationService.DefaultSubscriptionId;
        }

        return requested.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : requested;
    }

    private (DateTime From, DateTime To, string? Error) ResolveRange(HttpRequestData req)
    {
        var delay = Math.Clamp(_configuration.GetValue<int?>("COST_DATA_DELAY_DAYS") ?? 2, 1, 7);
        var defaultTo = DateTime.UtcNow.Date.AddDays(-delay);
        var defaultFrom = new DateTime(defaultTo.Year, defaultTo.Month, 1);
        var from = ParseDateOrDefault(req.Query?["from"], defaultFrom);
        var to = ParseDateOrDefault(req.Query?["to"], defaultTo);
        if (to < from)
        {
            (from, to) = (to, from);
        }

        return (to - from).TotalDays >= MaximumRangeDays
            ? (from, to, $"O período máximo permitido para o rateio é de {MaximumRangeDays} dias.")
            : (from, to, null);
    }

    private static DateTime ParseDateOrDefault(string? value, DateTime fallback) =>
        DateTime.TryParseExact(
            value,
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var parsed)
            ? parsed.Date
            : fallback.Date;

    public sealed class CostAllocationRulePayload
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("match")]
        public string? Match { get; set; }

        [JsonPropertyName("costCenter")]
        public string? CostCenter { get; set; }

        [JsonPropertyName("subscriptionId")]
        public string? SubscriptionId { get; set; }

        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }
}
