using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class DashboardSummaryFunction
{
    private const int SummaryFallbackDays = 7;
    private const int SummaryRegenerationLookbackDays = 30;

    private readonly IDashboardSummaryRepository _dashboardSummaryRepository;
    private readonly IAnalysisSnapshotService _analysisSnapshotService;
    private readonly SummaryGenerationService _summaryGenerationService;
    private readonly ICurrentUserService _currentUserService;
    private readonly AuthorizationService _authorizationService;
    private readonly ILogger<DashboardSummaryFunction> _logger;

    public DashboardSummaryFunction(
        IDashboardSummaryRepository dashboardSummaryRepository,
        IAnalysisSnapshotService analysisSnapshotService,
        SummaryGenerationService summaryGenerationService,
        ICurrentUserService currentUserService,
        AuthorizationService authorizationService,
        ILogger<DashboardSummaryFunction> logger)
    {
        _dashboardSummaryRepository = dashboardSummaryRepository;
        _analysisSnapshotService = analysisSnapshotService;
        _summaryGenerationService = summaryGenerationService;
        _currentUserService = currentUserService;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    [Function("DashboardSummaryGet")]
    public async Task<HttpResponseData> GetSummary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard/summary")] HttpRequestData req)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var authResult = await EnsureReaderAsync(req);
        if (authResult != null)
        {
            return authResult;
        }

        var date = ParseDate(req);

        try
        {
            var loadStopwatch = Stopwatch.StartNew();
            var (resolvedDate, global) = await GetSummaryWithFallbackAsync(DashboardSummaryTypes.GlobalDailySummary, date);

            var recommendation = await _dashboardSummaryRepository.GetByTypeAndDateAsync(DashboardSummaryTypes.RecommendationSummary, resolvedDate);
            var anomaly = await _dashboardSummaryRepository.GetByTypeAndDateAsync(DashboardSummaryTypes.AnomalySummary, resolvedDate);
            if (recommendation?.SnapshotRevision != global?.SnapshotRevision) recommendation = null;
            if (anomaly?.SnapshotRevision != global?.SnapshotRevision) anomaly = null;
            loadStopwatch.Stop();

            if (global == null)
            {
                var emptyResponse = req.CreateResponse(HttpStatusCode.OK);
                await emptyResponse.WriteAsJsonAsync(new
                {
                    date = date.ToString("yyyy-MM-dd"),
                    requestedDate = date.ToString("yyyy-MM-dd"),
                    periodId = date.ToString("yyyy-MM"),
                    periodFrom = new DateTime(date.Year, date.Month, 1).ToString("yyyy-MM-dd"),
                    periodTo = new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month)).ToString("yyyy-MM-dd"),
                    totalCost = (decimal?)null,
                    monthlyProjection = (decimal?)null,
                    financialSummary = new CostReportFinancialSummary(),
                    activeRecommendations = 0,
                    potentialMonthlySavings = 0m,
                    potentialAnnualSavings = 0m,
                    resolvedMonthlySavings = 0m,
                    falsePositiveMonthlySavings = 0m,
                    anomalyCount = 0,
                    criticalAnomalyCount = 0,
                    resourcesWithoutOwner = 0,
                    databaseCostTotal = (decimal?)null,
                    sqlCostTotal = (decimal?)null,
                    cosmosCostTotal = (decimal?)null,
                    recommendationStatusCounts = new Dictionary<string, int>(),
                    anomalySummary = (object?)null,
                    updatedAt = DateTime.UtcNow
                });
                return emptyResponse;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                date = global.Date,
                snapshotRevision = global.SnapshotRevision,
                requestedDate = date.ToString("yyyy-MM-dd"),
                periodId = global.PeriodId,
                periodFrom = global.PeriodFrom,
                periodTo = global.PeriodTo,
                totalCost = global.FinancialSummary?.TotalCost,
                monthlyProjection = global.FinancialSummary?.MonthlyProjection,
                financialSummary = global.FinancialSummary,
                activeRecommendations = global.ActiveRecommendations,
                potentialMonthlySavings = global.PotentialMonthlySavings,
                potentialAnnualSavings = global.PotentialAnnualSavings,
                resolvedMonthlySavings = global.ResolvedMonthlySavings,
                falsePositiveMonthlySavings = global.FalsePositiveMonthlySavings,
                anomalyCount = global.AnomalyCount,
                criticalAnomalyCount = global.CriticalAnomalyCount,
                resourcesWithoutOwner = global.ResourcesWithoutOwner,
                databaseCostTotal = global.DatabaseCostTotal,
                sqlCostTotal = global.SqlCostTotal,
                cosmosCostTotal = global.CosmosCostTotal,
                recommendationStatusCounts = recommendation?.StatusCounts ?? new Dictionary<string, int>(),
                anomalySummary = anomaly == null
                    ? null
                    : new
                    {
                        anomalyCount = anomaly.AnomalyCount,
                        criticalAnomalyCount = anomaly.CriticalAnomalyCount
                    },
                updatedAt = global.UpdatedAt
            });

            totalStopwatch.Stop();
            _logger.LogInformation(
                "[DASHBOARD-SUMMARY] date={date} loadMs={loadMs} totalMs={totalMs}",
                resolvedDate.ToString("yyyy-MM-dd"),
                loadStopwatch.ElapsedMilliseconds,
                totalStopwatch.ElapsedMilliseconds);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DASHBOARD-SUMMARY] Falha ao carregar summary para {date}", date.ToString("yyyy-MM-dd"));
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao carregar summary do dashboard.");
        }
    }

    [Function("DashboardTeamsGet")]
    public async Task<HttpResponseData> GetTeams(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard/teams")] HttpRequestData req)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var authResult = await EnsureReaderAsync(req);
        if (authResult != null)
        {
            return authResult;
        }

        var date = ParseDate(req);

        try
        {
            var loadStopwatch = Stopwatch.StartNew();
            var (resolvedDate, teams) = await GetTeamSummariesWithFallbackAsync(date);

            loadStopwatch.Stop();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                date = resolvedDate.ToString("yyyy-MM-dd"),
                snapshotRevision = teams.FirstOrDefault()?.SnapshotRevision,
                requestedDate = date.ToString("yyyy-MM-dd"),
                count = teams.Count,
                teams = teams.Select(item => new
                {
                    id = item.TeamId,
                    snapshotRevision = item.SnapshotRevision,
                    name = item.TeamName,
                    totalCost = item.FinancialSummary?.TotalCost,
                    monthlyProjection = item.FinancialSummary?.MonthlyProjection,
                    financialSummary = item.FinancialSummary,
                    activeRecommendations = item.ActiveRecommendations,
                    potentialMonthlySavings = item.PotentialMonthlySavings,
                    potentialAnnualSavings = item.PotentialAnnualSavings,
                    anomalyCount = item.AnomalyCount,
                    criticalAnomalyCount = item.CriticalAnomalyCount,
                    resourcesWithoutOwner = item.ResourcesWithoutOwner,
                    sqlCostTotal = item.SqlCostTotal,
                    cosmosCostTotal = item.CosmosCostTotal,
                    updatedAt = item.UpdatedAt
                })
            });

            totalStopwatch.Stop();
            _logger.LogInformation(
                "[DASHBOARD-TEAMS] date={date} count={count} loadMs={loadMs} totalMs={totalMs}",
                resolvedDate.ToString("yyyy-MM-dd"),
                teams.Count,
                loadStopwatch.ElapsedMilliseconds,
                totalStopwatch.ElapsedMilliseconds);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DASHBOARD-TEAMS] Falha ao carregar resumo por time para {date}", date.ToString("yyyy-MM-dd"));
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao carregar resumo por time.");
        }
    }

    [Function("DashboardDatabasesGet")]
    public async Task<HttpResponseData> GetDatabases(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard/databases")] HttpRequestData req)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var authResult = await EnsureReaderAsync(req);
        if (authResult != null)
        {
            return authResult;
        }

        var date = ParseDate(req);

        try
        {
            var loadStopwatch = Stopwatch.StartNew();
            var (resolvedDate, summary) = await GetSummaryWithFallbackAsync(DashboardSummaryTypes.DatabaseSummary, date);

            loadStopwatch.Stop();

            if (summary == null)
            {
                var emptyResponse = req.CreateResponse(HttpStatusCode.OK);
                await emptyResponse.WriteAsJsonAsync(new
                {
                    date = date.ToString("yyyy-MM-dd"),
                    requestedDate = date.ToString("yyyy-MM-dd"),
                    sqlCostTotal = (decimal?)null,
                    cosmosCostTotal = (decimal?)null,
                    databaseCostTotal = (decimal?)null,
                    activeSqlRecommendations = 0,
                    activeCosmosRecommendations = 0,
                    lowConfidenceCostCount = 0,
                    notMatchedCostCount = 0,
                    updatedAt = DateTime.UtcNow
                });
                return emptyResponse;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                date = summary.Date,
                snapshotRevision = summary.SnapshotRevision,
                requestedDate = date.ToString("yyyy-MM-dd"),
                sqlCostTotal = summary.SqlCostTotal,
                cosmosCostTotal = summary.CosmosCostTotal,
                databaseCostTotal = summary.DatabaseCostTotal,
                activeSqlRecommendations = summary.ActiveSqlRecommendations,
                activeCosmosRecommendations = summary.ActiveCosmosRecommendations,
                lowConfidenceCostCount = summary.LowConfidenceCostCount,
                notMatchedCostCount = summary.NotMatchedCostCount,
                updatedAt = summary.UpdatedAt
            });

            totalStopwatch.Stop();
            _logger.LogInformation(
                "[DASHBOARD-DATABASES] date={date} loadMs={loadMs} totalMs={totalMs}",
                resolvedDate.ToString("yyyy-MM-dd"),
                loadStopwatch.ElapsedMilliseconds,
                totalStopwatch.ElapsedMilliseconds);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DASHBOARD-DATABASES] Falha ao carregar resumo de bancos para {date}", date.ToString("yyyy-MM-dd"));
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao carregar resumo de bancos.");
        }
    }

    private async Task<HttpResponseData?> EnsureReaderAsync(HttpRequestData req)
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

        return null;
    }

    private static DateTime ParseDate(HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        return DateTime.TryParse(query["date"], out var parsed)
            ? parsed.Date
            : DateTime.UtcNow.Date;
    }

    private async Task<(DateTime ResolvedDate, DashboardSummaryDocument? Summary)> GetSummaryWithFallbackAsync(string type, DateTime requestedDate)
    {
        var (date, global) = await DashboardSnapshotSelection.ResolveAsync(_dashboardSummaryRepository, requestedDate);
        if (type == DashboardSummaryTypes.GlobalDailySummary) return (date, global);
        var scoped = global == null ? null : await _dashboardSummaryRepository.GetByTypeAndDateAsync(type, date);
        return (date, scoped?.SnapshotRevision == global?.SnapshotRevision ? scoped : null);
    }

    private async Task<(DateTime ResolvedDate, IReadOnlyList<DashboardSummaryDocument> Teams)> GetTeamSummariesWithFallbackAsync(DateTime requestedDate)
    {
        var (resolvedDate, global) = await GetSummaryWithFallbackAsync(DashboardSummaryTypes.GlobalDailySummary, requestedDate);
        return (resolvedDate, global == null ? Array.Empty<DashboardSummaryDocument>()
            : (await _dashboardSummaryRepository.ListByTypeAndDateAsync(DashboardSummaryTypes.TeamDailySummary, resolvedDate))
                .Where(t => t.SnapshotRevision == global.SnapshotRevision).ToList());
    }

}
