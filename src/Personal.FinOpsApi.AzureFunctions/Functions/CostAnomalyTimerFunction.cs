using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Globalization;
using System.Diagnostics;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// Timer e API manual para detecção de anomalias de custo diário.
/// 
/// Timer: Roda diariamente às 08:00 UTC (configurável via CostAnomalySchedule)
/// API:   GET /api/cost-anomalies?date=2026-05-14 (consulta relatório salvo)
/// API:   POST /api/cost-anomalies/run (execução manual)
/// 
/// Fluxo:
/// 1. Consulta custo diário das subscriptions via Cost Management API
/// 2. Compara hoje vs últimos 3 dias (baseline)
/// 3. Compara hoje vs meta diária (budget / 30)
/// 4. Calcula projeção mensal
/// 5. Gera JSON de anomalias no Blob
/// </summary>
public class CostAnomalyTimerFunction
{
    private readonly CostAnomalyDailyCostService _dailyCostService;
    private readonly CostAnomalyAnalysisService _analysisService;
    private readonly CostAnomalyStorageService _costAnomalyStorageService;
    private readonly ICostAnomalyReportRepository _costAnomalyReportRepository;
    private readonly SubscriptionDiscoveryService _subscriptionDiscoveryService;
    private readonly SubscriptionBillingCycleService _subscriptionBillingCycleService;
    private readonly ITeamSubscriptionsLookupService _teamSubscriptionsLookupService;
    private readonly IOwnershipConfigReader _ownershipConfigReader;
    private readonly CurrentUserService _currentUserService;
    private readonly AuthorizationService _authorizationService;
    private readonly ICostAnomalyQueueService _anomalyQueueService;
    private readonly SummaryGenerationService _summaryGenerationService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostAnomalyTimerFunction> _logger;

    public CostAnomalyTimerFunction(
        CostAnomalyDailyCostService dailyCostService,
        CostAnomalyAnalysisService analysisService,
        CostAnomalyStorageService costAnomalyStorageService,
        ICostAnomalyReportRepository costAnomalyReportRepository,
        SubscriptionDiscoveryService subscriptionDiscoveryService,
        SubscriptionBillingCycleService subscriptionBillingCycleService,
        ITeamSubscriptionsLookupService teamSubscriptionsLookupService,
        IOwnershipConfigReader ownershipConfigReader,
        CurrentUserService currentUserService,
        AuthorizationService authorizationService,
        ICostAnomalyQueueService anomalyQueueService,
        SummaryGenerationService summaryGenerationService,
        IConfiguration configuration,
        ILogger<CostAnomalyTimerFunction> logger)
    {
        _dailyCostService = dailyCostService;
        _analysisService = analysisService;
        _costAnomalyStorageService = costAnomalyStorageService;
        _costAnomalyReportRepository = costAnomalyReportRepository;
        _subscriptionDiscoveryService = subscriptionDiscoveryService;
        _subscriptionBillingCycleService = subscriptionBillingCycleService;
        _teamSubscriptionsLookupService = teamSubscriptionsLookupService;
        _ownershipConfigReader = ownershipConfigReader;
        _currentUserService = currentUserService;
        _authorizationService = authorizationService;
        _anomalyQueueService = anomalyQueueService;
        _summaryGenerationService = summaryGenerationService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Timer diário para detecção de anomalias de custo
    /// </summary>
    [Function("CostAnomalyDailyTimer")]
    public async Task RunTimer([TimerTrigger("%CostAnomalySchedule%")] TimerInfo timer)
    {
        var enabled = _configuration["EnableCostAnomalyAnalysis"];
        if (string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[COST-ANOMALY-TIMER] Feature desabilitada (EnableCostAnomalyAnalysis=false)");
            return;
        }
        _logger.LogInformation("[COST-ANOMALY-TIMER] Iniciando análise de anomalias de custo diário");
        await _anomalyQueueService.EnqueueRunAsync("timer");
    }

    /// <summary>
    /// Execução manual da análise de anomalias
    /// POST /api/cost-anomalies/run
    /// </summary>
    [Function("CostAnomalyManualRun")]
    public async Task<HttpResponseData> ManualRun(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "cost-anomalies/run")] HttpRequestData req)
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

        var enabled = _configuration["EnableCostAnomalyAnalysis"];
        if (string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            var disabledResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await disabledResponse.WriteAsJsonAsync(new { message = "Feature desabilitada (EnableCostAnomalyAnalysis=false)" });
            return disabledResponse;
        }

        _logger.LogInformation("[COST-ANOMALY] Execução manual iniciada");

        try
        {
            var runId = await _anomalyQueueService.EnqueueRunAsync(user.UserPrincipalName);

            var response = req.CreateResponse(HttpStatusCode.Accepted);
            await response.WriteAsJsonAsync(new
            {
                message = "Análise de anomalias enfileirada para execução em background.",
                runId,
                queue = _anomalyQueueService.RunQueueName
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[COST-ANOMALY] Erro na execução manual");
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao executar análise de anomalias.");
        }
    }

    [Function("CostAnomalyRunStatus")]
    public async Task<HttpResponseData> GetRunStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost-anomalies/runs/{runId}")] HttpRequestData req,
        string runId)
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
            var manifest = await _costAnomalyStorageService.LoadRunManifestAsync(runId);
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                runId = manifest.RunId,
                status = manifest.Status,
                totalSubscriptions = manifest.Subscriptions.Count,
                completedSubscriptions = manifest.CompletedSubscriptions,
                failedSubscriptions = manifest.FailedSubscriptions,
                requestedDate = manifest.RequestedDate,
                updatedAt = manifest.UpdatedAt,
                completedAt = manifest.CompletedAt,
                summaryGeneratedAt = manifest.SummaryGeneratedAt
            });
            return response;
        }
        catch (ArgumentException)
        {
            return await ApiHttpResponses.WriteErrorAsync(
                req,
                HttpStatusCode.BadRequest,
                "InvalidRunId",
                "RunId inválido.");
        }
        catch (InvalidOperationException)
        {
            return await ApiHttpResponses.NotFound(req, "Execução de anomalias não encontrada.");
        }
    }

    [Function("CostAnomalyQueuedRun")]
    public async Task RunQueued(
        [ServiceBusTrigger("%QUEUE_COST_ANOMALY_RUN%", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[COST-ANOMALY] Execução enfileirada iniciada. messageId={messageId} body={body}",
            message.MessageId,
            message.Body.ToString());

        var request = JsonSerializer.Deserialize<CostAnomalyRunRequest>(
            message.Body.ToString(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Mensagem de execução de anomalias inválida.");
        if (!Guid.TryParse(request.RequestId, out _))
        {
            throw new InvalidOperationException("RequestId da execução de anomalias é inválido.");
        }

        var config = CostAnomalyConfig.FromConfiguration(_configuration);
        var requestedDate = request.RequestedAt.ToUniversalTime().Date;
        var subscriptions = await ResolveSubscriptionsAsync();
        var excludedSubscriptions = GetExcludedSubscriptions();
        subscriptions = subscriptions
            .Where(subscriptionId => !excludedSubscriptions.Contains(subscriptionId))
            .ToList();
        var subscriptionNames = await ResolveSubscriptionNamesAsync(subscriptions);
        var manifest = new CostAnomalyRunManifest
        {
            RunId = request.RequestId,
            RequestedAt = request.RequestedAt,
            RequestedDate = requestedDate.ToString("yyyy-MM-dd"),
            TriggeredBy = request.TriggeredBy,
            Config = config,
            Subscriptions = subscriptions
                .Select(subscriptionId => new CostAnomalyRunSubscription
                {
                    SubscriptionId = subscriptionId,
                    SubscriptionName = subscriptionNames.GetValueOrDefault(subscriptionId, subscriptionId)
                })
                .ToList()
        };

        await _costAnomalyStorageService.SaveRunManifestAsync(manifest, cancellationToken);
        if (manifest.Subscriptions.Count == 0)
        {
            var emptyReport = CreateReport(requestedDate, config, 0);
            await _costAnomalyReportRepository.SaveReportAsync(emptyReport, cancellationToken);
            manifest.Status = CostAnomalyRunStatuses.Completed;
            manifest.CompletedAt = DateTime.UtcNow;
            manifest.UpdatedAt = manifest.CompletedAt.Value;
            await _costAnomalyStorageService.SaveRunManifestAsync(manifest, cancellationToken);
            await GenerateSummaryAsync(manifest, requestedDate, cancellationToken);
            _logger.LogWarning("[COST-ANOMALY] Execução {runId} concluída sem subscriptions.", manifest.RunId);
            return;
        }

        await _anomalyQueueService.EnqueueSubscriptionAsync(manifest.RunId, 0, cancellationToken);
        _logger.LogInformation(
            "[COST-ANOMALY] Execução {runId} segmentada em {count} subscriptions.",
            manifest.RunId,
            manifest.Subscriptions.Count);
    }

    [Function("CostAnomalySubscriptionRun")]
    public async Task RunSubscriptionQueued(
        [ServiceBusTrigger("%QUEUE_COST_ANOMALY_SUBSCRIPTION%", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message,
        CancellationToken cancellationToken)
    {
        var workItem = JsonSerializer.Deserialize<CostAnomalySubscriptionWorkItem>(
            message.Body.ToString(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Mensagem de subscription de anomalias inválida.");
        var manifest = await _costAnomalyStorageService.LoadRunManifestAsync(workItem.RunId, cancellationToken);
        if (workItem.SubscriptionIndex < 0 || workItem.SubscriptionIndex >= manifest.Subscriptions.Count)
        {
            throw new InvalidOperationException(
                $"Índice {workItem.SubscriptionIndex} inválido para a execução {workItem.RunId}.");
        }
        if (manifest.CompletedSubscriptions > workItem.SubscriptionIndex)
        {
            if (manifest.Status == CostAnomalyRunStatuses.Completed &&
                !manifest.SummaryGeneratedAt.HasValue &&
                workItem.SubscriptionIndex == manifest.Subscriptions.Count - 1)
            {
                var completedDate = DateTime.ParseExact(
                    manifest.RequestedDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);
                await GenerateSummaryAsync(manifest, completedDate, cancellationToken);
            }
            if (manifest.Status != CostAnomalyRunStatuses.Completed &&
                manifest.CompletedSubscriptions < manifest.Subscriptions.Count)
            {
                await _anomalyQueueService.EnqueueSubscriptionAsync(
                    manifest.RunId,
                    manifest.CompletedSubscriptions,
                    cancellationToken);
            }
            _logger.LogInformation(
                "[COST-ANOMALY] Entrega repetida da execução {runId}, índice {index}; progresso retomado em {nextIndex}.",
                manifest.RunId,
                workItem.SubscriptionIndex,
                manifest.CompletedSubscriptions);
            return;
        }

        var requestedDate = DateTime.ParseExact(
            manifest.RequestedDate,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);
        var subscription = manifest.Subscriptions[workItem.SubscriptionIndex];
        var result = await AnalyzeSubscriptionAsync(
            subscription.SubscriptionId,
            subscription.SubscriptionName,
            requestedDate,
            manifest.Config,
            cancellationToken);
        await _costAnomalyStorageService.SaveRunSubscriptionResultAsync(
            manifest.RunId,
            workItem.SubscriptionIndex,
            result,
            cancellationToken);

        var completed = workItem.SubscriptionIndex + 1;
        manifest.CompletedSubscriptions = completed;
        if (result.DataStatus == CostAnomalyDataStatus.QueryFailed)
        {
            manifest.FailedSubscriptions++;
        }
        manifest.UpdatedAt = DateTime.UtcNow;
        await _costAnomalyStorageService.SaveRunManifestAsync(manifest, cancellationToken);

        _logger.LogInformation(
            "[COST-ANOMALY] Execução {runId}: subscription {subscriptionId} concluída ({completed}/{total}) status={status}.",
            manifest.RunId,
            subscription.SubscriptionId,
            completed,
            manifest.Subscriptions.Count,
            result.DataStatus);

        if (completed < manifest.Subscriptions.Count)
        {
            await _anomalyQueueService.EnqueueSubscriptionAsync(manifest.RunId, completed, cancellationToken);
            return;
        }

        var results = await _costAnomalyStorageService.LoadRunSubscriptionResultsAsync(
            manifest.RunId,
            manifest.Subscriptions.Count,
            cancellationToken);
        var report = CreateReport(requestedDate, manifest.Config, manifest.Subscriptions.Count);
        report.Subscriptions = results;
        FinalizeReport(report, manifest.Config);
        await _costAnomalyReportRepository.SaveReportAsync(report, cancellationToken);
        manifest.Status = CostAnomalyRunStatuses.Completed;
        manifest.CompletedAt = DateTime.UtcNow;
        manifest.UpdatedAt = manifest.CompletedAt.Value;
        await _costAnomalyStorageService.SaveRunManifestAsync(manifest, cancellationToken);
        await GenerateSummaryAsync(manifest, requestedDate, cancellationToken);
        _logger.LogInformation(
            "[COST-ANOMALY] Execução {runId} concluída: {total} subscriptions, {anomalies} anomalias.",
            manifest.RunId,
            report.TotalSubscriptionsAnalyzed,
            report.TotalAnomaliesDetected);
    }

    private async Task GenerateSummaryAsync(
        CostAnomalyRunManifest manifest,
        DateTime requestedDate,
        CancellationToken cancellationToken)
    {
        if (manifest.SummaryGeneratedAt.HasValue)
        {
            return;
        }

        await _summaryGenerationService.GenerateDailySummariesAsync(requestedDate, cancellationToken);
        manifest.SummaryGeneratedAt = DateTime.UtcNow;
        manifest.UpdatedAt = manifest.SummaryGeneratedAt.Value;
        await _costAnomalyStorageService.SaveRunManifestAsync(manifest, cancellationToken);
        _logger.LogInformation(
            "[COST-ANOMALY] Snapshot financeiro regenerado após a execução {runId} para {date}.",
            manifest.RunId,
            manifest.RequestedDate);
    }

    /// <summary>
    /// Consulta relatório de anomalias salvo no blob
    /// GET /api/cost-anomalies?date=2026-05-14
    /// GET /api/cost-anomalies?days=3
    /// </summary>
    [Function("CostAnomalyGet")]
    public async Task<HttpResponseData> GetAnomalyReport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost-anomalies")] HttpRequestData req)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var authStopwatch = Stopwatch.StartNew();
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanRead(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão de leitura.");
        }
        authStopwatch.Stop();

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var date = query["date"] ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var hasExplicitDateFilter = !string.IsNullOrWhiteSpace(query["date"]);
        var subscriptionIdFilter = query["subscriptionId"];
        var teamFilter = query["team"];
        var severityFilter = query["severity"];
        var excludedSubscriptions = GetExcludedSubscriptions();
        var days = int.TryParse(query["days"], out var parsedDays)
            ? Math.Clamp(parsedDays, 1, 30)
            : 3;

        try
        {
            var blobReadStopwatch = Stopwatch.StartNew();
            var baseDate = DateTime.TryParse(date, out var parsedDate) ? parsedDate.Date : DateTime.UtcNow.Date;
            var reports = (await _costAnomalyReportRepository.LoadReportsAsync(baseDate, days)).ToList();
            blobReadStopwatch.Stop();

            if (reports.Count == 0)
            {
                var latestReport = await _costAnomalyReportRepository.LoadLatestReportAsync();
                if (latestReport != null &&
                    (!hasExplicitDateFilter || IsReportUsableForRequestedDate(latestReport, baseDate)))
                {
                    reports.Add(latestReport);
                    _logger.LogInformation(
                        "[COST-ANOMALY] Snapshot da data não encontrado. Usando último relatório válido para date={date} days={days} latestDate={latestDate}",
                        date,
                        days,
                        latestReport.Date);
                }
            }

            if (reports.Count == 0)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteAsJsonAsync(new
                {
                    error = "CostAnomaliesSnapshotNotFound",
                    message = "Dados de anomalias ainda não foram gerados para esta data.",
                    date
                });
                return notFoundResponse;
            }

            var teamLookupStopwatch = Stopwatch.StartNew();
            var teamMappings = await _teamSubscriptionsLookupService.GetAllSubscriptionTeamMappingsAsync();
            var ownership = await _ownershipConfigReader.GetConfigAsync();
            var teamNamesById = ownership.Teams
                .Where(team => !string.IsNullOrWhiteSpace(team.Id))
                .GroupBy(team => team.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
            teamLookupStopwatch.Stop();

            var processingStopwatch = Stopwatch.StartNew();
            // Aplicar filtros opcionais e retornar lista tabular para consumidores HTTP.
            var flattened = reports.SelectMany(report => report.Subscriptions.Select(s =>
            {
                var cycle = ResolveCurrentCycleSnapshot(s, report);
                var cycleDailyBudget = ResolveCycleDailyBudget(cycle, report);
                return new
                {
                    date = report.Date,
                    requestedDate = string.IsNullOrWhiteSpace(s.RequestedDate) ? report.RequestedDate : s.RequestedDate,
                    analysisDate = string.IsNullOrWhiteSpace(s.AnalysisDate) ? report.AnalysisDate : s.AnalysisDate,
                    baselineStartDate = s.BaselineStartDate,
                    baselineEndDate = s.BaselineEndDate,
                    costSource = s.CostSource,
                    costDataAsOf = s.CostDataAsOf,
                    costUsedFallback = s.CostUsedFallback,
                    costFallbackReason = s.CostFallbackReason,
                    dailyBudget = cycleDailyBudget,
                    subscriptionId = s.SubscriptionId,
                    subscriptionName = s.SubscriptionName,
                    latestDailyCost = s.LatestDailyCost,
                    baselineAverage = s.BaselineAverage,
                    todayCost = s.TodayCost, // legacy alias
                    averageLastDays = s.AverageLast3Days, // legacy alias
                    increaseAmount = s.IncreaseAmount,
                    increasePercent = s.IncreasePercent,
                    monthlyProjection = s.MonthlyProjection,
                    projectedOverBudget = s.ProjectedOverBudget,
                    currentCycleCost = cycle.CurrentCycleCost,
                    currentCycleStartDate = cycle.CurrentCycleStartDate,
                    currentCycleTurnDate = cycle.CurrentCycleTurnDate,
                    currentCycleElapsedDays = cycle.CurrentCycleElapsedDays,
                    currentCycleRemainingDays = cycle.CurrentCycleRemainingDays,
                    currentCycleDailyAverage = cycle.CurrentCycleDailyAverage,
                    currentCycleSource = cycle.CurrentCycleSource,
                    currentCycleConfidence = cycle.CurrentCycleConfidence,
                    currentCycleNote = cycle.CurrentCycleNote,
                    severity = s.Severity,
                    hasAnomaly = s.HasAnomaly,
                    dataStatus = s.DataStatus,
                    reasons = s.Reasons,
                    teamId = teamMappings.TryGetValue(s.SubscriptionId, out var teamId) ? teamId : null,
                    teamName = teamMappings.TryGetValue(s.SubscriptionId, out var mappedTeamId)
                        && teamNamesById.TryGetValue(mappedTeamId, out var teamName)
                            ? teamName
                            : null
                };
            }));

            flattened = flattened.Where(s => !excludedSubscriptions.Contains(s.subscriptionId));

            if (!string.IsNullOrEmpty(subscriptionIdFilter))
                flattened = flattened.Where(s => s.subscriptionId.Equals(subscriptionIdFilter, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(severityFilter))
                flattened = flattened.Where(s => s.severity.Equals(severityFilter, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(teamFilter))
                flattened = flattened.Where(s => (s.teamId ?? string.Empty).Equals(teamFilter, StringComparison.OrdinalIgnoreCase));

            var result = flattened
                .OrderByDescending(s => s.date)
                .ThenByDescending(s => s.increasePercent)
                .ToList();
            processingStopwatch.Stop();

            var serializationStopwatch = Stopwatch.StartNew();
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(result).Length;
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(result);
            serializationStopwatch.Stop();

            _logger.LogInformation(
                "[COST-ANOMALY] GET concluído date={date} days={days} total={totalMs}ms auth={authMs}ms blobRead={blobReadMs}ms teamLookup={teamLookupMs}ms processing={processingMs}ms serialize={serializeMs}ms items={items} payloadBytes={payloadBytes}",
                date,
                days,
                totalStopwatch.ElapsedMilliseconds,
                authStopwatch.ElapsedMilliseconds,
                blobReadStopwatch.ElapsedMilliseconds,
                teamLookupStopwatch.ElapsedMilliseconds,
                processingStopwatch.ElapsedMilliseconds,
                serializationStopwatch.ElapsedMilliseconds,
                result.Count,
                payloadBytes);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[COST-ANOMALY] Erro ao consultar relatório de anomalias");
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao consultar relatório de anomalias.");
        }
    }

    private async Task<SubscriptionCostAnomaly> AnalyzeSubscriptionAsync(
        string subscriptionId,
        string subscriptionName,
        DateTime requestedDate,
        CostAnomalyConfig config,
        CancellationToken cancellationToken)
    {
        try
        {
            var billingCycleReferenceDate = ResolveBillingCycleReferenceDate(config, requestedDate);
            var billingCycleWindow = await _subscriptionBillingCycleService.ResolveAsync(
                subscriptionId,
                billingCycleReferenceDate,
                cancellationToken);
            var cycleWindow = new SubscriptionBillingCycleWindow
            {
                CycleStartDate = DateTime.ParseExact(
                    billingCycleWindow.CurrentPeriodFrom,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture),
                CycleTurnDate = DateTime.ParseExact(
                    billingCycleWindow.CurrentPeriodTo,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture).AddDays(1),
                Source = billingCycleWindow.Source,
                Confidence = billingCycleWindow.Confidence,
                Note = billingCycleWindow.Note
            };
            var lookbackDays = ResolveLookbackDays(config, requestedDate, cycleWindow);
            var dailyCosts = await _dailyCostService.GetDailyCostsAsync(
                subscriptionId,
                billingCycleReferenceDate,
                lookbackDays,
                cancellationToken);
            return _analysisService.Analyze(
                subscriptionId,
                subscriptionName,
                dailyCosts,
                requestedDate,
                config,
                cycleWindow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[COST-ANOMALY] Falha nas fontes de custo da subscription {subscriptionId}; registrando status QueryFailed",
                subscriptionId);
            return new SubscriptionCostAnomaly
            {
                SubscriptionId = subscriptionId,
                SubscriptionName = subscriptionName,
                RequestedDate = requestedDate.ToString("yyyy-MM-dd"),
                DataStatus = CostAnomalyDataStatus.QueryFailed,
                Reasons = new List<string>
                {
                    "A leitura do Cost Management Export e o fallback temporário da Query API falharam. Consulte os logs da execução."
                }
            };
        }
    }

    internal static bool IsReportUsableForRequestedDate(CostAnomalyReport report, DateTime requestedDate)
    {
        var reportDateText = string.IsNullOrWhiteSpace(report.Date)
            ? report.RequestedDate
            : report.Date;
        return DateTime.TryParseExact(
                reportDateText,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out _)
            && string.CompareOrdinal(
                reportDateText,
                requestedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) <= 0;
    }

    private static CostAnomalyReport CreateReport(
        DateTime requestedDate,
        CostAnomalyConfig config,
        int totalSubscriptions)
    {
        return new CostAnomalyReport
        {
            Date = requestedDate.ToString("yyyy-MM-dd"),
            RequestedDate = requestedDate.ToString("yyyy-MM-dd"),
            MonthlyBudget = config.MonthlyBudget,
            DailyBudget = config.DailyBudget,
            BaselineDays = config.BaselineDays,
            TotalSubscriptionsAnalyzed = totalSubscriptions
        };
    }

    private static void FinalizeReport(CostAnomalyReport report, CostAnomalyConfig config)
    {
        report.TotalAnomaliesDetected = report.Subscriptions.Count(s => s.HasAnomaly);
        var latestAnalysisDate = report.Subscriptions
            .Where(s => s.DataStatus == CostAnomalyDataStatus.Consolidated)
            .Select(s => DateTime.TryParse(s.AnalysisDate, out var parsed) ? parsed.Date : DateTime.MinValue)
            .Where(d => d != DateTime.MinValue)
            .DefaultIfEmpty()
            .Max();

        if (latestAnalysisDate != default)
        {
            report.AnalysisDate = latestAnalysisDate.ToString("yyyy-MM-dd");
            report.BaselineStartDate = latestAnalysisDate.AddDays(-config.BaselineDays).ToString("yyyy-MM-dd");
            report.BaselineEndDate = latestAnalysisDate.AddDays(-1).ToString("yyyy-MM-dd");
        }
    }

    private async Task<List<string>> ResolveSubscriptionsAsync()
    {
        var raw = _configuration["COST_SUBSCRIPTIONS"];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        try
        {
            var discovered = await _subscriptionDiscoveryService.DiscoverSubscriptionsAsync();
            if (discovered.Count > 0)
            {
                _logger.LogInformation("[COST-ANOMALY] Discovery automático: {count} subscriptions", discovered.Count);
                return discovered.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[COST-ANOMALY] Falha no discovery automático");
        }

        var single = _configuration["AZURE_SUBSCRIPTION_ID"];
        if (!string.IsNullOrWhiteSpace(single))
            return new List<string> { single.Trim() };

        _logger.LogWarning("[COST-ANOMALY] Nenhuma subscription configurada");
        return new List<string>();
    }

    private static int ResolveLookbackDays(
        CostAnomalyConfig config,
        DateTime requestedDate,
        SubscriptionBillingCycleWindow? billingCycleWindow)
    {
        var referenceDate = config.IgnoreCurrentDay
            ? requestedDate.Date.AddDays(-1)
            : requestedDate.Date;

        var lookbackDays = Math.Max(config.LookbackDays, requestedDate.Day);
        if (billingCycleWindow is not null &&
            billingCycleWindow.CycleStartDate.Date <= referenceDate &&
            referenceDate < billingCycleWindow.CycleTurnDate.Date)
        {
            var cycleDaysToFetch = Math.Max(1, (referenceDate - billingCycleWindow.CycleStartDate.Date).Days + 1);
            lookbackDays = Math.Max(lookbackDays, cycleDaysToFetch);
        }

        return lookbackDays;
    }

    private static DateTime ResolveBillingCycleReferenceDate(CostAnomalyConfig config, DateTime requestedDate)
    {
        return config.IgnoreCurrentDay
            ? requestedDate.Date.AddDays(-1)
            : requestedDate.Date;
    }

    private async Task<Dictionary<string, string>> ResolveSubscriptionNamesAsync(List<string> subscriptionIds)
    {
        var names = new Dictionary<string, string>();
        try
        {
            var details = await _subscriptionDiscoveryService.GetSubscriptionDetailsAsync(subscriptionIds);
            foreach (var (id, detail) in details)
            {
                // detail é anonymous type com display_name
                var json = System.Text.Json.JsonSerializer.Serialize(detail);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("display_name", out var displayName))
                {
                    names[id] = displayName.GetString() ?? id;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[COST-ANOMALY] Falha ao resolver nomes das subscriptions");
        }

        return names;
    }

    private HashSet<string> GetExcludedSubscriptions()
    {
        var raw = _configuration["CostAnomalyExcludedSubscriptions"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static CurrentCycleSnapshot ResolveCurrentCycleSnapshot(
        SubscriptionCostAnomaly subscription,
        CostAnomalyReport report,
        SubscriptionBillingCycleWindow? liveCycleWindow = null)
    {
        var referenceDate = TryParseDate(subscription.AnalysisDate)
            ?? TryParseDate(report.AnalysisDate)
            ?? TryParseDate(report.Date)
            ?? DateTime.UtcNow.Date;

        if (liveCycleWindow is not null &&
            liveCycleWindow.CycleStartDate.Date <= referenceDate &&
            referenceDate < liveCycleWindow.CycleTurnDate.Date)
        {
            return BuildCurrentCycleSnapshot(subscription, referenceDate, liveCycleWindow.CycleStartDate.Date, liveCycleWindow.CycleTurnDate.Date, liveCycleWindow.Source);
        }

        if (!string.IsNullOrWhiteSpace(subscription.CurrentCycleTurnDate))
        {
            return new CurrentCycleSnapshot(
                subscription.CurrentCycleCost,
                subscription.CurrentCycleStartDate,
                subscription.CurrentCycleTurnDate,
                subscription.CurrentCycleElapsedDays,
                subscription.CurrentCycleRemainingDays,
                subscription.CurrentCycleDailyAverage,
                subscription.CurrentCycleSource,
                subscription.CurrentCycleConfidence,
                subscription.CurrentCycleNote);
        }

        var cycleStart = new DateTime(referenceDate.Year, referenceDate.Month, 1);
        var cycleTurn = cycleStart.AddMonths(1);
        return BuildCurrentCycleSnapshot(subscription, referenceDate, cycleStart, cycleTurn, BillingPeriodSources.DefaultCalendarMonth);
    }

    private static CurrentCycleSnapshot BuildCurrentCycleSnapshot(
        SubscriptionCostAnomaly subscription,
        DateTime referenceDate,
        DateTime cycleStart,
        DateTime cycleTurn,
        string source,
        string? confidence = null,
        string? note = null)
    {
        var elapsedDays = Math.Max(1, (referenceDate - cycleStart).Days + 1);
        var remainingDays = Math.Max(0, (cycleTurn - referenceDate).Days);

        var currentCycleCost = subscription.DailyCosts
            .Select(entry => new
            {
                Entry = entry,
                ParsedDate = DateTime.TryParseExact(
                    entry.Date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedDate)
                        ? parsedDate.Date
                        : DateTime.MinValue
            })
            .Where(x => x.ParsedDate != DateTime.MinValue && x.ParsedDate >= cycleStart && x.ParsedDate <= referenceDate)
            .Sum(x => x.Entry.TotalCost);

        var currentCycleDailyAverage = currentCycleCost > 0 ? currentCycleCost / elapsedDays : 0m;

        return new CurrentCycleSnapshot(
            currentCycleCost,
            cycleStart.ToString("yyyy-MM-dd"),
            cycleTurn.ToString("yyyy-MM-dd"),
            elapsedDays,
            remainingDays,
            currentCycleDailyAverage,
            source,
            confidence ?? ResolveSourceConfidence(source),
            note ?? ResolveSourceNote(source));
    }

    private static DateTime? TryParseDate(string? dateText)
    {
        if (string.IsNullOrWhiteSpace(dateText))
        {
            return null;
        }

        return DateTime.TryParse(dateText, out var parsedDate)
            ? parsedDate.Date
            : null;
    }

    private static decimal ResolveCycleDailyBudget(CurrentCycleSnapshot cycle, CostAnomalyReport report)
    {
        var cycleStart = TryParseDate(cycle.CurrentCycleStartDate);
        var cycleTurn = TryParseDate(cycle.CurrentCycleTurnDate);
        if (cycleStart is null || cycleTurn is null || report.MonthlyBudget <= 0)
        {
            return report.DailyBudget;
        }

        var cycleLengthDays = Math.Max(1, (cycleTurn.Value - cycleStart.Value).Days);
        return report.MonthlyBudget / cycleLengthDays;
    }

    private readonly record struct CurrentCycleSnapshot(
        decimal CurrentCycleCost,
        string CurrentCycleStartDate,
        string CurrentCycleTurnDate,
        int CurrentCycleElapsedDays,
        int CurrentCycleRemainingDays,
        decimal CurrentCycleDailyAverage,
        string CurrentCycleSource,
        string CurrentCycleConfidence,
        string CurrentCycleNote);

    private static string ResolveSourceConfidence(string source)
    {
        return source switch
        {
            BillingPeriodSources.AzureBillingPeriodsApi => BillingPeriodConfidence.High,
            BillingPeriodSources.ManualConfiguration => BillingPeriodConfidence.Medium,
            _ => BillingPeriodConfidence.Low
        };
    }

    private static string ResolveSourceNote(string source)
    {
        return source switch
        {
            BillingPeriodSources.AzureBillingPeriodsApi => "Billing period oficial retornado pela Azure BillingPeriods API.",
            BillingPeriodSources.ManualConfiguration => "Billing period configurado manualmente para a subscription.",
            _ => "Billing period oficial não encontrado; usando mês calendário."
        };
    }
}
