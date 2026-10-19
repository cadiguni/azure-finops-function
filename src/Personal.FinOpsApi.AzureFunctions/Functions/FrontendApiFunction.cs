using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// APIs padronizadas para o frontend FinOps Portal.
/// Complementam as APIs existentes com endpoints otimizados para o SPA.
/// </summary>
public class FrontendApiFunction
{
    private const int DefaultHistoryDays = 30;
    private const int MaxHistoryDays = 120;
    private const int RecommendationsFallbackDays = 7;

    private readonly IAnalysisSnapshotService _storageService;
    private readonly ITeamSubscriptionsLookupService _teamSubscriptionsService;
    private readonly IRecommendationStatusRepository _recommendationStatusRepository;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly AuthorizationService _authorizationService;
    private readonly ILogger<FrontendApiFunction> _logger;
    private readonly IDashboardSummaryRepository? _dashboardSummaries;
    private readonly IReportSnapshotStore? _reportSnapshots;

    public FrontendApiFunction(
        IAnalysisSnapshotService storageService,
        ITeamSubscriptionsLookupService teamSubscriptionsService,
        IRecommendationStatusRepository recommendationStatusRepository,
        IAuditLogRepository auditLogRepository,
        ICurrentUserService currentUserService,
        AuthorizationService authorizationService,
        ILogger<FrontendApiFunction> logger,
        IDashboardSummaryRepository? dashboardSummaries = null,
        IReportSnapshotStore? reportSnapshots = null)
    {
        _storageService = storageService;
        _teamSubscriptionsService = teamSubscriptionsService;
        _recommendationStatusRepository = recommendationStatusRepository;
        _auditLogRepository = auditLogRepository;
        _currentUserService = currentUserService;
        _authorizationService = authorizationService;
        _logger = logger;
        _dashboardSummaries = dashboardSummaries;
        _reportSnapshots = reportSnapshots;
    }

    [Function("GetMe")]
    public async Task<HttpResponseData> GetMe(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "me")] HttpRequestData req)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        var roles = user.Roles
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            userId = user.UserId,
            userPrincipalName = user.UserPrincipalName,
            displayName = user.DisplayName,
            roles,
            isReader = _authorizationService.CanRead(user),
            isAdmin = _authorizationService.CanEdit(user)
        });
        return response;
    }

    /// <summary>
    /// Retorna recomendações do dia com filtros opcionais.
    /// GET /api/recommendations?date=YYYY-MM-DD&subscriptionId=xxx
    /// </summary>
    [Function("GetRecommendations")]
    public async Task<HttpResponseData> GetRecommendations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "recommendations")] HttpRequestData req)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanRead(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão de leitura.");
        }

        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var dateParam = query["date"];
            var subscriptionId = query["subscriptionId"];
            var statusFilter = query["status"];
            var view = query["view"] ?? "all";
            var hasManualActionFilter = ParseNullableBoolean(query["hasManualAction"]);
            var historyDays = ParseHistoryDays(query["historyDays"], defaultDays: 1);

            var requestedDate = DateTime.TryParse(dateParam, out var parsed)
                ? parsed.Date
                : DateTime.UtcNow.Date;

            var recommendationsLoadStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var (date, recommendations, snapshotRevision, financialSummary, periodFrom, periodTo) =
                await LoadRecommendationsSnapshotAsync(requestedDate, subscriptionId);
            recommendationsLoadStopwatch.Stop();

            var historyStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var occurrenceStats = historyDays > 1
                ? await BuildOccurrenceStatsAsync(date, historyDays)
                : new Dictionary<string, RecommendationOccurrenceStats>(StringComparer.OrdinalIgnoreCase);
            historyStopwatch.Stop();

            var enrichmentStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var recommendationKeys = recommendations
                .Select(r => RecommendationStatusKeyBuilder.Build(r.ResourceId, r.Type))
                .ToArray();
            var statusByKey = await _recommendationStatusRepository.GetByIdsAsync(recommendationKeys);
            var subscriptionToTeam = await _teamSubscriptionsService.GetAllSubscriptionTeamMappingsAsync();
            enrichmentStopwatch.Stop();

            var utcNow = DateTime.UtcNow;
            var projectedRecommendations = recommendations
                .Select(r =>
                {
                    var key = RecommendationStatusKeyBuilder.Build(r.ResourceId, r.Type);
                    statusByKey.TryGetValue(key, out var recStatus);
                    occurrenceStats.TryGetValue(key, out var occurrence);
                    var (effectiveStatus, effectiveReason) = ResolveEffectiveReadState(recStatus, utcNow);
                    var fallbackDetectedAt = r.LastEvaluationDate == default ? (DateTime?)null : r.LastEvaluationDate;
                    var firstDetectedAt = occurrence?.FirstDetectedAt ?? fallbackDetectedAt;
                    var lastDetectedAt = occurrence?.LastDetectedAt ?? fallbackDetectedAt;
                    var occurrenceCount = occurrence?.OccurrenceCount ?? (fallbackDetectedAt.HasValue ? 1 : 0);
                    var hasManualAction = recStatus != null;
                    return new
                    {
                        id = key,
                        resourceId = r.ResourceId,
                        resourceName = r.ResourceName,
                        resourceType = r.ResourceType,
                        resourceGroup = r.ResourceGroup,
                        subscriptionId = r.SubscriptionId,
                        teamId = subscriptionToTeam.TryGetValue(r.SubscriptionId, out var teamId) ? teamId : null,
                        type = r.Type,
                        severity = r.Priority,
                        priority = r.Priority,
                        description = r.Description,
                        recommendation = r.Recommendation,
                        estimatedMonthlySavings = r.EstimatedMonthlySavings,
                        savingsStatus = string.IsNullOrWhiteSpace(r.SavingsStatus)
                            ? (r.EstimatedMonthlySavings > 0 ? "Quantified" : "NotEstimated")
                            : r.SavingsStatus,
                        dailyCost = r.DailyCost,
                        estimatedMonthlyCost = r.EstimatedMonthlyCost,
                        costAmount = r.CostAmount ?? (r.EstimatedMonthlyCost > 0 ? r.EstimatedMonthlyCost : null),
                        observedCost = r.ObservedCost,
                        costProvenance = r.CostProvenance,
                        costCurrency = string.IsNullOrWhiteSpace(r.CostCurrency) ? r.Currency : r.CostCurrency,
                        costPeriodFrom = r.CostPeriodFrom,
                        costPeriodTo = r.CostPeriodTo,
                        costSource = string.IsNullOrWhiteSpace(r.CostSource) ? CostMatchSources.NotMatched : r.CostSource,
                        costMatchConfidence = string.IsNullOrWhiteSpace(r.CostMatchConfidence) ? CostMatchConfidenceLevels.Low : r.CostMatchConfidence,
                        costMatchedResourceId = r.CostMatchedResourceId,
                        costNote = string.IsNullOrWhiteSpace(r.CostNote)
                            ? "Não foi possível associar custo para este recurso no período consultado."
                            : r.CostNote,
                        confidence = r.Confidence,
                        impact = r.Impact,
                        lastEvaluationDate = fallbackDetectedAt,
                        firstDetectedAt,
                        lastDetectedAt,
                        occurrenceCount,
                        status = effectiveStatus,
                        statusSource = hasManualAction ? "Manual" : "System",
                        hasManualAction,
                        statusReason = effectiveReason,
                        statusUpdatedBy = recStatus?.UpdatedBy,
                        statusUpdatedAt = recStatus?.UpdatedAt,
                        lastStatusUpdatedBy = recStatus?.UpdatedBy,
                        lastStatusUpdatedAt = recStatus?.UpdatedAt,
                        snoozeUntil = recStatus?.SnoozeUntil
                    };
                })
                .ToList();

            IEnumerable<dynamic> filteredRecommendations = projectedRecommendations;

            if (!string.IsNullOrWhiteSpace(statusFilter))
            {
                filteredRecommendations = filteredRecommendations
                    .Where(item => string.Equals((string)item.status, statusFilter, StringComparison.OrdinalIgnoreCase));
            }

            if (hasManualActionFilter.HasValue)
            {
                filteredRecommendations = filteredRecommendations
                    .Where(item => (bool)item.hasManualAction == hasManualActionFilter.Value);
            }

            filteredRecommendations = NormalizeView(view) switch
            {
                "active" => filteredRecommendations.Where(item => IsActiveStatus((string)item.status)),
                "actions" => filteredRecommendations.Where(item => (bool)item.hasManualAction),
                "resolved" => filteredRecommendations.Where(item => string.Equals((string)item.status, RecommendationStatuses.Resolved, StringComparison.OrdinalIgnoreCase)),
                _ => filteredRecommendations
            };

            var recommendationsResult = filteredRecommendations
                .OrderByDescending(item => (decimal)item.estimatedMonthlySavings)
                .ToList();

            var summary = new
            {
                date = date.ToString("yyyy-MM-dd"),
                requestedDate = requestedDate.ToString("yyyy-MM-dd"),
                snapshotRevision,
                financialSummary,
                periodFrom,
                periodTo,
                totalRecommendations = recommendationsResult.Count,
                totalEstimatedMonthlySavings = recommendationsResult.Sum(r => (decimal)r.estimatedMonthlySavings),
                totalEstimatedAnnualSavings = recommendationsResult.Sum(r => (decimal)r.estimatedMonthlySavings) * 12,
                byType = recommendationsResult
                    .GroupBy(r => (string)r.type)
                    .Select(g => new
                    {
                        type = g.Key,
                        count = g.Count(),
                        estimatedMonthlySavings = g.Sum(r => (decimal)r.estimatedMonthlySavings)
                    })
                    .OrderByDescending(x => x.estimatedMonthlySavings),
                bySubscription = recommendationsResult
                    .GroupBy(r => (string)r.subscriptionId)
                    .Select(g => new
                    {
                        subscriptionId = g.Key,
                        count = g.Count(),
                        estimatedMonthlySavings = g.Sum(r => (decimal)r.estimatedMonthlySavings)
                    })
                    .OrderByDescending(x => x.estimatedMonthlySavings),
                recommendations = recommendationsResult
            };

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(summary);

            _logger.LogInformation(
                "[FRONTEND-API] /recommendations concluído date={date} historyDays={historyDays} total={totalMs}ms load={loadMs}ms history={historyMs}ms enrich={enrichMs}ms totalRecommendations={count}",
                date.ToString("yyyy-MM-dd"),
                historyDays,
                totalStopwatch.ElapsedMilliseconds,
                recommendationsLoadStopwatch.ElapsedMilliseconds,
                historyStopwatch.ElapsedMilliseconds,
                enrichmentStopwatch.ElapsedMilliseconds,
                recommendationsResult.Count);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FRONTEND-API] Erro ao buscar recomendações");
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao buscar recomendações.");
        }
    }

    [Function("GetRecommendationDetails")]
    public async Task<HttpResponseData> GetRecommendationDetails(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "recommendations/{id}")] HttpRequestData req,
        string id)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanRead(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão de leitura.");
        }

        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var date = DateTime.TryParse(query["date"], out var parsed) ? parsed.Date : DateTime.UtcNow.Date;
            var snapshotRevision = query["snapshotRevision"];
            var historyDays = ParseHistoryDays(query["historyDays"]);
            var occurrences = await BuildOccurrencesAsync(id, date, historyDays);
            var status = await _recommendationStatusRepository.GetByIdAsync(id);
            var auditLogs = await _auditLogRepository.GetByEntityIdAsync(id, 200);
            var subscriptionToTeam = await _teamSubscriptionsService.GetAllSubscriptionTeamMappingsAsync();

            if (occurrences.Count == 0 && status == null)
            {
                return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.NotFound, "NotFound", "Recomendação não encontrada.");
            }

            var latestOccurrence = occurrences
                .OrderByDescending(o => o.DetectedAt)
                .FirstOrDefault();
            var firstDetectedAt = occurrences.Count > 0 ? occurrences.Min(o => o.DetectedAt) : (DateTime?)null;
            var lastDetectedAt = occurrences.Count > 0 ? occurrences.Max(o => o.DetectedAt) : (DateTime?)null;
            var occurrenceCount = occurrences.Count;
            var representative = !string.IsNullOrWhiteSpace(snapshotRevision)
                ? await LoadRecommendationFromSnapshotAsync(snapshotRevision, id)
                : null;
            representative ??= latestOccurrence?.Recommendation;

            var fallbackTeamId = !string.IsNullOrWhiteSpace(status?.SubscriptionId)
                && subscriptionToTeam.TryGetValue(status.SubscriptionId, out var mappedTeamId)
                ? mappedTeamId
                : status?.TeamId;
            var teamId = representative != null
                ? (subscriptionToTeam.TryGetValue(representative.SubscriptionId, out var teamFromRecommendation) ? teamFromRecommendation : status?.TeamId)
                : fallbackTeamId;

            var (effectiveStatus, effectiveReason) = ResolveEffectiveReadState(status, DateTime.UtcNow);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                id,
                recommendation = new
                {
                    id,
                    resourceId = representative?.ResourceId ?? status?.ResourceId,
                    resourceName = representative?.ResourceName ?? string.Empty,
                    resourceType = representative?.ResourceType ?? string.Empty,
                    resourceGroup = representative?.ResourceGroup ?? status?.ResourceGroup,
                    subscriptionId = representative?.SubscriptionId ?? status?.SubscriptionId,
                    teamId,
                    type = representative?.Type ?? status?.RecommendationType,
                    severity = representative?.Priority ?? "Low",
                    priority = representative?.Priority ?? "Low",
                    description = representative?.Description ?? string.Empty,
                    recommendation = representative?.Recommendation ?? string.Empty,
                    estimatedMonthlySavings = representative?.EstimatedMonthlySavings ?? 0,
                    savingsStatus = string.IsNullOrWhiteSpace(representative?.SavingsStatus)
                        ? (representative?.EstimatedMonthlySavings ?? 0) > 0 ? "Quantified" : "NotEstimated"
                        : representative.SavingsStatus,
                    dailyCost = representative?.DailyCost ?? 0,
                    estimatedMonthlyCost = representative?.EstimatedMonthlyCost ?? 0,
                    costAmount = representative?.CostAmount ?? (representative?.EstimatedMonthlyCost > 0 ? representative.EstimatedMonthlyCost : null),
                    observedCost = representative?.ObservedCost,
                    costProvenance = representative?.CostProvenance,
                    costCurrency = !string.IsNullOrWhiteSpace(representative?.CostCurrency)
                        ? representative.CostCurrency
                        : representative?.Currency ?? "BRL",
                    costPeriodFrom = representative?.CostPeriodFrom,
                    costPeriodTo = representative?.CostPeriodTo,
                    costSource = !string.IsNullOrWhiteSpace(representative?.CostSource)
                        ? representative.CostSource
                        : CostMatchSources.NotMatched,
                    costMatchConfidence = !string.IsNullOrWhiteSpace(representative?.CostMatchConfidence)
                        ? representative.CostMatchConfidence
                        : CostMatchConfidenceLevels.Low,
                    costMatchedResourceId = representative?.CostMatchedResourceId,
                    costNote = !string.IsNullOrWhiteSpace(representative?.CostNote)
                        ? representative.CostNote
                        : "Não foi possível associar custo para este recurso no período consultado.",
                    confidence = representative?.Confidence ?? 0,
                    impact = representative?.Impact ?? string.Empty,
                    firstDetectedAt,
                    lastDetectedAt,
                    occurrenceCount,
                    status = effectiveStatus,
                    statusReason = effectiveReason,
                    statusUpdatedBy = status?.UpdatedBy,
                    statusUpdatedAt = status?.UpdatedAt,
                    lastStatusUpdatedBy = status?.UpdatedBy,
                    lastStatusUpdatedAt = status?.UpdatedAt,
                    snoozeUntil = status?.SnoozeUntil
                },
                status = status == null
                    ? null
                    : new
                    {
                        id = status.Id,
                        status = effectiveStatus,
                        reason = effectiveReason,
                        snoozeUntil = status.SnoozeUntil,
                        updatedBy = status.UpdatedBy,
                        updatedAt = status.UpdatedAt,
                        createdAt = status.CreatedAt
                    },
                ownership = new
                {
                    teamId,
                    subscriptionId = representative?.SubscriptionId ?? status?.SubscriptionId
                },
                firstDetectedAt,
                lastDetectedAt,
                occurrenceCount,
                isRecurring = occurrenceCount > 1,
                hasSufficientHistory = occurrenceCount > 1 || auditLogs.Count > 0,
                historyWindowDays = historyDays,
                occurrencesRecent = occurrences
                    .OrderByDescending(o => o.DetectedAt)
                    .Take(20)
                    .Select(o => new
                    {
                        detectedAt = o.DetectedAt,
                        resourceId = o.Recommendation.ResourceId,
                        subscriptionId = o.Recommendation.SubscriptionId,
                        estimatedMonthlySavings = o.Recommendation.EstimatedMonthlySavings,
                        severity = o.Recommendation.Priority,
                        description = o.Recommendation.Description
                    }),
                auditLogs = auditLogs
                    .OrderByDescending(log => log.PerformedAt)
                    .Select(log => new
                    {
                        id = log.Id,
                        action = log.Action,
                        performedBy = log.PerformedBy,
                        performedAt = log.PerformedAt,
                        before = log.Before,
                        after = log.After,
                        metadata = log.Metadata
                    })
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FRONTEND-API] Erro ao buscar detalhe da recomendação {id}", id);
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao buscar detalhe da recomendação.");
        }
    }

    [Function("GetRecommendationHistory")]
    public async Task<HttpResponseData> GetRecommendationHistory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "recommendations/{id}/history")] HttpRequestData req,
        string id)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanRead(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão de leitura.");
        }

        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var limit = ParseLimit(query["limit"]);
            var auditLogs = await _auditLogRepository.GetByEntityIdAsync(id, limit);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                recommendationId = id,
                total = auditLogs.Count,
                items = auditLogs
                    .OrderByDescending(log => log.PerformedAt)
                    .Select(log => new
                    {
                        id = log.Id,
                        action = log.Action,
                        performedBy = log.PerformedBy,
                        performedAt = log.PerformedAt,
                        before = log.Before,
                        after = log.After,
                        metadata = log.Metadata
                    })
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FRONTEND-API] Erro ao buscar histórico da recomendação {id}", id);
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao buscar histórico da recomendação.");
        }
    }

    [Function("PatchRecommendationStatus")]
    public async Task<HttpResponseData> PatchRecommendationStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "recommendations/{id}/status")] HttpRequestData req,
        string id)
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

        var payload = JsonSerializer.Deserialize<RecommendationStatusPatchRequest>(
            await req.ReadAsStringAsync() ?? "{}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (payload == null || !RecommendationStatuses.IsValid(payload.Status))
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", "status inválido.");
        }

        var existing = await _recommendationStatusRepository.GetByIdAsync(id);
        if (existing == null &&
            (string.IsNullOrWhiteSpace(payload.ResourceId) || string.IsNullOrWhiteSpace(payload.RecommendationType)))
        {
            return await ApiHttpResponses.WriteErrorAsync(
                req,
                HttpStatusCode.BadRequest,
                "BadRequest",
                "resourceId e recommendationType são obrigatórios para criar status.");
        }

        if (existing == null)
        {
            var expectedId = RecommendationStatusKeyBuilder.Build(payload.ResourceId!, payload.RecommendationType!);
            if (!id.Equals(expectedId, StringComparison.OrdinalIgnoreCase))
            {
                return await ApiHttpResponses.WriteErrorAsync(
                    req,
                    HttpStatusCode.BadRequest,
                    "BadRequest",
                    "id inválido para a combinação resourceId + recommendationType.");
            }
        }

        if (payload.Status.Equals(RecommendationStatuses.Snoozed, StringComparison.OrdinalIgnoreCase) && payload.SnoozeUntil == null)
        {
            return await ApiHttpResponses.WriteErrorAsync(
                req,
                HttpStatusCode.BadRequest,
                "BadRequest",
                "snoozeUntil é obrigatório quando status = Snoozed.");
        }

        var actor = ResolveActor(user);
        var utcNow = DateTime.UtcNow;
        var statusDocument = existing ?? new RecommendationStatusDocument
        {
            Id = id,
            ResourceId = payload.ResourceId ?? string.Empty,
            SubscriptionId = payload.SubscriptionId ?? string.Empty,
            ResourceGroup = payload.ResourceGroup ?? string.Empty,
            TeamId = payload.TeamId,
            RecommendationType = payload.RecommendationType ?? string.Empty,
            CreatedAt = utcNow
        };

        statusDocument.Status = payload.Status;
        statusDocument.Reason = payload.Reason;
        statusDocument.SnoozeUntil = payload.ClearSnooze
            ? null
            : payload.Status.Equals(RecommendationStatuses.Snoozed, StringComparison.OrdinalIgnoreCase)
                ? payload.SnoozeUntil
                : null;
        statusDocument.UpdatedAt = utcNow;
        statusDocument.UpdatedBy = actor;

        await _recommendationStatusRepository.UpsertAsync(statusDocument);

        await _auditLogRepository.AppendAsync(new AuditLogDocument
        {
            EntityType = "recommendationStatus",
            EntityId = id,
            Action = existing == null ? "Created" : "StatusChanged",
            PerformedBy = actor,
            PerformedAt = utcNow,
            Before = existing,
            After = statusDocument
        });

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            id = statusDocument.Id,
            type = statusDocument.Type,
            resourceId = statusDocument.ResourceId,
            subscriptionId = statusDocument.SubscriptionId,
            resourceGroup = statusDocument.ResourceGroup,
            teamId = statusDocument.TeamId,
            recommendationType = statusDocument.RecommendationType,
            status = statusDocument.Status,
            reason = statusDocument.Reason,
            snoozeUntil = statusDocument.SnoozeUntil,
            updatedBy = statusDocument.UpdatedBy,
            updatedAt = statusDocument.UpdatedAt,
            createdAt = statusDocument.CreatedAt
        });
        return response;
    }

    private static string ResolveActor(CurrentUserContext user)
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

    internal static (string Status, string? Reason) ResolveEffectiveReadState(RecommendationStatusDocument? status, DateTime utcNow)
    {
        if (status == null || string.IsNullOrWhiteSpace(status.Status))
        {
            return (RecommendationStatuses.New, null);
        }

        if (status.Status.Equals(RecommendationStatuses.Snoozed, StringComparison.OrdinalIgnoreCase)
            && status.SnoozeUntil.HasValue
            && status.SnoozeUntil.Value <= utcNow)
        {
            return (RecommendationStatuses.New, null);
        }

        return (status.Status, status.Reason);
    }

    private static int ParseHistoryDays(string? rawValue, int defaultDays = DefaultHistoryDays)
    {
        if (!int.TryParse(rawValue, out var parsed))
        {
            return Math.Clamp(defaultDays, 1, MaxHistoryDays);
        }

        return Math.Clamp(parsed, 1, MaxHistoryDays);
    }

    private static bool? ParseNullableBoolean(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        return bool.TryParse(rawValue, out var parsed) ? parsed : null;
    }

    private static string NormalizeView(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return "all";
        }

        var normalized = rawValue.Trim().ToLowerInvariant();
        return normalized is "active" or "actions" or "resolved" or "all"
            ? normalized
            : "all";
    }

    private static bool IsActiveStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        return status.Equals(RecommendationStatuses.New, StringComparison.OrdinalIgnoreCase)
               || status.Equals(RecommendationStatuses.Active, StringComparison.OrdinalIgnoreCase)
               || status.Equals(RecommendationStatuses.InReview, StringComparison.OrdinalIgnoreCase)
               || status.Equals(RecommendationStatuses.Accepted, StringComparison.OrdinalIgnoreCase)
               || status.Equals(RecommendationStatuses.ApprovedForCleanup, StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseLimit(string? rawValue)
    {
        if (!int.TryParse(rawValue, out var parsed))
        {
            return 100;
        }

        return Math.Clamp(parsed, 1, 500);
    }

    private async Task<Dictionary<string, RecommendationOccurrenceStats>> BuildOccurrenceStatsAsync(DateTime endDateUtc, int historyDays)
    {
        var stats = new Dictionary<string, RecommendationOccurrenceStats>(StringComparer.OrdinalIgnoreCase);
        var startDateUtc = endDateUtc.Date.AddDays(-(historyDays - 1));

        for (var date = startDateUtc; date <= endDateUtc.Date; date = date.AddDays(1))
        {
            var dailyRecommendations = await _storageService.GetDailyAnalysisAsync(date);
            foreach (var recommendation in dailyRecommendations)
            {
                var key = RecommendationStatusKeyBuilder.Build(recommendation.ResourceId, recommendation.Type);
                var detectedAt = ResolveDetectedAt(recommendation, date);

                if (!stats.TryGetValue(key, out var stat))
                {
                    stats[key] = new RecommendationOccurrenceStats
                    {
                        FirstDetectedAt = detectedAt,
                        LastDetectedAt = detectedAt,
                        OccurrenceCount = 1
                    };
                    continue;
                }

                stat.OccurrenceCount += 1;
                if (detectedAt < stat.FirstDetectedAt)
                {
                    stat.FirstDetectedAt = detectedAt;
                }

                if (detectedAt > stat.LastDetectedAt)
                {
                    stat.LastDetectedAt = detectedAt;
                }
            }
        }

        return stats;
    }

    private async Task<List<RecommendationOccurrence>> BuildOccurrencesAsync(string recommendationId, DateTime endDateUtc, int historyDays)
    {
        var occurrences = new List<RecommendationOccurrence>();
        var startDateUtc = endDateUtc.Date.AddDays(-(historyDays - 1));

        for (var date = startDateUtc; date <= endDateUtc.Date; date = date.AddDays(1))
        {
            var dailyRecommendations = await _storageService.GetDailyAnalysisAsync(date);
            foreach (var recommendation in dailyRecommendations)
            {
                var key = RecommendationStatusKeyBuilder.Build(recommendation.ResourceId, recommendation.Type);
                if (!key.Equals(recommendationId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                occurrences.Add(new RecommendationOccurrence
                {
                    DetectedAt = ResolveDetectedAt(recommendation, date),
                    Recommendation = recommendation
                });
            }
        }

        return occurrences;
    }

    private async Task<(DateTime ResolvedDate, List<CostRecommendation> Recommendations, string? SnapshotRevision,
        CostReportFinancialSummary? FinancialSummary, string PeriodFrom, string PeriodTo)> LoadRecommendationsSnapshotAsync(
        DateTime requestedDate,
        string? subscriptionId)
    {
        if (_dashboardSummaries == null || _reportSnapshots == null)
        {
            var legacy = await LoadRecommendationsWithFallbackAsync(requestedDate, subscriptionId);
            return (legacy.ResolvedDate, legacy.Recommendations, null, null, string.Empty, string.Empty);
        }

        var (resolvedDate, summary) = await DashboardSnapshotSelection.ResolveAsync(_dashboardSummaries, requestedDate);
        if (summary?.SnapshotRevision == null)
            return (resolvedDate, [], null, summary?.FinancialSummary, summary?.PeriodFrom ?? string.Empty, summary?.PeriodTo ?? string.Empty);

        var scope = string.IsNullOrWhiteSpace(subscriptionId) ? "global" : $"subscription:{subscriptionId.Trim()}";
        var report = await _reportSnapshots.LoadAsync(summary.SnapshotRevision, scope);
        if (report == null)
            return (resolvedDate, [], summary.SnapshotRevision, summary.FinancialSummary, summary.PeriodFrom, summary.PeriodTo);

        return (report.AnalysisDate, FlattenReport(report), report.SnapshotRevision,
            report.FinancialSummary, report.PeriodFrom, report.PeriodTo);
    }

    private async Task<CostRecommendation?> LoadRecommendationFromSnapshotAsync(string revision, string recommendationId)
    {
        if (_reportSnapshots == null) return null;
        RecommendationReport? report;
        try
        {
            report = await _reportSnapshots.LoadAsync(revision, "global");
        }
        catch (InvalidDataException)
        {
            return null;
        }
        return report == null ? null : FlattenReport(report).FirstOrDefault(r =>
            RecommendationStatusKeyBuilder.Build(r.ResourceId, r.Type).Equals(recommendationId, StringComparison.OrdinalIgnoreCase));
    }

    private static List<CostRecommendation> FlattenReport(RecommendationReport report)
    {
        var recommendations = new List<CostRecommendation>();
        foreach (var managementGroup in report.ManagementGroups)
        foreach (var subscription in managementGroup.Subscriptions)
        foreach (var resourceGroup in subscription.ResourceGroups)
        foreach (var item in resourceGroup.Recommendations)
        {
            recommendations.Add(new CostRecommendation
            {
                Type = item.RecommendationType,
                ResourceId = item.ResourceId,
                ResourceName = item.ResourceName,
                ResourceType = item.ResourceType,
                ResourceGroup = resourceGroup.Name,
                SubscriptionId = subscription.Id,
                ManagementGroupId = managementGroup.Id == "unassigned" ? null : managementGroup.Id,
                Location = resourceGroup.Location,
                Description = item.Description,
                Recommendation = item.RecommendationText,
                Impact = item.Impact,
                Priority = item.Priority,
                Confidence = decimal.TryParse(item.Confidence.TrimEnd('%'),
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var confidence)
                    ? confidence / 100m : 0m,
                EstimatedMonthlySavings = item.PotentialSavings,
                SavingsStatus = item.SavingsStatus,
                EstimatedMonthlyCost = item.CurrentCost,
                DailyCost = item.DailyCost,
                CostAmount = item.ObservedCost,
                ObservedCost = item.ObservedCost,
                CostCurrency = item.Currency,
                Currency = item.Currency,
                CostPeriodFrom = item.CostPeriodFrom,
                CostPeriodTo = item.CostPeriodTo,
                CostSource = item.CostSource,
                CostMatchConfidence = item.CostMatchConfidence,
                CostMatchedResourceId = item.CostMatchedResourceId,
                CostNote = item.CostNote,
                CostProvenance = item.CostProvenance,
                LastEvaluationDate = report.AnalysisDate
            });
        }
        return recommendations;
    }

    private async Task<(DateTime ResolvedDate, List<CostRecommendation> Recommendations)> LoadRecommendationsWithFallbackAsync(
        DateTime requestedDate,
        string? subscriptionId)
    {
        var normalizedSubscriptionId = string.IsNullOrWhiteSpace(subscriptionId)
            ? null
            : subscriptionId.Trim();

        for (var offset = 0; offset <= RecommendationsFallbackDays; offset++)
        {
            var candidateDate = requestedDate.Date.AddDays(-offset);
            var dailyRecommendations = await _storageService.GetDailyAnalysisAsync(candidateDate);
            var filtered = normalizedSubscriptionId == null
                ? dailyRecommendations
                : dailyRecommendations
                    .Where(r => r.SubscriptionId.Equals(normalizedSubscriptionId, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (filtered.Count > 0)
            {
                if (offset > 0)
                {
                    _logger.LogWarning(
                        "[FRONTEND-RECOMMENDATIONS] Sem dados em {requestedDate}. Usando fallback {resolvedDate}.",
                        requestedDate.ToString("yyyy-MM-dd"),
                        candidateDate.ToString("yyyy-MM-dd"));
                }

                return (candidateDate, filtered);
            }
        }

        return (requestedDate.Date, new List<CostRecommendation>());
    }

    private static DateTime ResolveDetectedAt(CostRecommendation recommendation, DateTime fallbackDate)
    {
        return recommendation.LastEvaluationDate == default
            ? fallbackDate.Date
            : recommendation.LastEvaluationDate;
    }
}

internal sealed class RecommendationOccurrenceStats
{
    public DateTime FirstDetectedAt { get; set; }
    public DateTime LastDetectedAt { get; set; }
    public int OccurrenceCount { get; set; }
}

internal sealed class RecommendationOccurrence
{
    public DateTime DetectedAt { get; set; }
    public CostRecommendation Recommendation { get; set; } = new();
}

public class RecommendationStatusPatchRequest
{
    public string Status { get; set; } = RecommendationStatuses.New;
    public string? Reason { get; set; }
    public DateTime? SnoozeUntil { get; set; }
    public bool ClearSnooze { get; set; }
    public string? ResourceId { get; set; }
    public string? SubscriptionId { get; set; }
    public string? ResourceGroup { get; set; }
    public string? TeamId { get; set; }
    public string? RecommendationType { get; set; }
}
