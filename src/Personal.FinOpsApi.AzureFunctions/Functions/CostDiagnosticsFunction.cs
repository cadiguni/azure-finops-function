using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class CostDiagnosticsFunction
{
    private readonly ResourceCostLookupService _resourceCostLookupService;
    private readonly IAnalysisSnapshotService _analysisSnapshotService;
    private readonly ICurrentUserService _currentUserService;
    private readonly AuthorizationService _authorizationService;
    private readonly ILogger<CostDiagnosticsFunction> _logger;

    public CostDiagnosticsFunction(
        ResourceCostLookupService resourceCostLookupService,
        IAnalysisSnapshotService analysisSnapshotService,
        ICurrentUserService currentUserService,
        AuthorizationService authorizationService,
        ILogger<CostDiagnosticsFunction> logger)
    {
        _resourceCostLookupService = resourceCostLookupService;
        _analysisSnapshotService = analysisSnapshotService;
        _currentUserService = currentUserService;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    [Function("GetCostMatchDiagnostics")]
    public async Task<HttpResponseData> GetCostMatchDiagnostics(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "diagnostics/cost-match")] HttpRequestData req)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.IsAdmin(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão administrativa.");
        }

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var resourceId = query["resourceId"];
        var periodId = query["periodId"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return await ApiHttpResponses.WriteErrorAsync(
                req,
                HttpStatusCode.BadRequest,
                "BadRequest",
                "resourceId é obrigatório.");
        }

        try
        {
            var resolution = await _resourceCostLookupService.GetCostMatchDiagnosticsAsync(resourceId, periodId);
            var profile = await ResolveResourceProfileAsync(resourceId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                resourceId = profile.ResourceId,
                resourceName = profile.ResourceName,
                resourceType = profile.ResourceType,
                subscriptionId = profile.SubscriptionId,
                resourceGroup = profile.ResourceGroup,
                periodId,
                matchesByResourceId = resolution.ResourceIdMatches.Select(ToOutputCandidate),
                matchesByResourceNameAndResourceGroup = resolution.ResourceNameGroupMatches.Select(ToOutputCandidate),
                matchesByResourceType = resolution.ResourceTypeMatches.Select(ToOutputCandidate),
                matchesByServiceNameAndMeterCategory = resolution.ServiceMeterMatches.Select(ToOutputCandidate),
                bestMatch = resolution.BestMatch == null ? null : ToOutputCandidate(resolution.BestMatch),
                confidence = resolution.CostMatchConfidence,
                costSource = resolution.CostSource,
                costAmount = resolution.MonthlyCost,
                observedCost = resolution.BestMatch?.TotalCost,
                costProvenance = resolution.Provenance,
                currency = resolution.Currency,
                costPeriodFrom = resolution.PeriodFromUtc,
                costPeriodTo = resolution.PeriodToUtc,
                costMatchedResourceId = resolution.CostMatchedResourceId,
                observations = resolution.Observations,
                note = resolution.CostNote
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[COST-DIAG] Falha ao executar diagnóstico para {resourceId}", resourceId);
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao executar diagnóstico de custo.");
        }
    }

    private async Task<(string ResourceId, string ResourceName, string ResourceType, string SubscriptionId, string ResourceGroup)> ResolveResourceProfileAsync(string resourceId)
    {
        var parsed = ParseResourceId(resourceId);
        for (var i = 0; i < 7; i++)
        {
            var date = DateTime.UtcNow.Date.AddDays(-i);
            var dailyRecommendations = await _analysisSnapshotService.GetDailyAnalysisAsync(date);
            var recommendation = dailyRecommendations.FirstOrDefault(r =>
                string.Equals(r.ResourceId?.TrimEnd('/'), resourceId.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            if (recommendation != null)
            {
                return (
                    recommendation.ResourceId,
                    recommendation.ResourceName,
                    recommendation.ResourceType,
                    recommendation.SubscriptionId,
                    recommendation.ResourceGroup);
            }
        }

        return parsed;
    }

    private static object ToOutputCandidate(ResourceCostCandidate candidate)
    {
        return new
        {
            source = candidate.Source,
            confidence = candidate.Confidence,
            matchedResourceId = candidate.MatchedResourceId,
            resourceType = candidate.ResourceType,
            resourceGroup = candidate.ResourceGroup,
            serviceName = candidate.ServiceName,
            meterCategory = candidate.MeterCategory,
            meterSubCategory = candidate.MeterSubCategory,
            totalCost = candidate.TotalCost,
            currency = candidate.Currency,
            daysWithCost = candidate.DaysWithCost,
            matchReason = candidate.MatchReason
        };
    }

    private static (string ResourceId, string ResourceName, string ResourceType, string SubscriptionId, string ResourceGroup) ParseResourceId(string resourceId)
    {
        var normalized = resourceId.Trim().TrimEnd('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var subscriptionId = string.Empty;
        var resourceGroup = string.Empty;
        var resourceName = parts.Length > 0 ? parts[^1] : string.Empty;
        var resourceType = string.Empty;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("subscriptions", StringComparison.OrdinalIgnoreCase))
            {
                subscriptionId = parts[i + 1];
            }
            if (parts[i].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
            {
                resourceGroup = parts[i + 1];
            }
        }

        var providersIndex = Array.FindIndex(parts, p => p.Equals("providers", StringComparison.OrdinalIgnoreCase));
        if (providersIndex >= 0 && providersIndex + 2 < parts.Length)
        {
            var typeParts = new List<string> { parts[providersIndex + 1] };
            for (var i = providersIndex + 2; i < parts.Length; i += 2)
            {
                typeParts.Add(parts[i]);
            }
            resourceType = string.Join('/', typeParts);
        }

        return (normalized, resourceName, resourceType, subscriptionId, resourceGroup);
    }
}
