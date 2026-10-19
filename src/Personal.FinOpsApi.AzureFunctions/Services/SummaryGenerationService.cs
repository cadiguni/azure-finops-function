using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public class SummaryGenerationService
{
    private static readonly HashSet<string> ActiveStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        RecommendationStatuses.Active,
        RecommendationStatuses.New,
        RecommendationStatuses.InReview,
        RecommendationStatuses.Accepted,
        RecommendationStatuses.ApprovedForCleanup
    };

    private readonly IAnalysisSnapshotService _analysisSnapshotService;
    private readonly IRecommendationStatusRepository _recommendationStatusRepository;
    private readonly ICostAnomalyReportRepository _costAnomalyReportRepository;
    private readonly IOwnershipRepository _ownershipRepository;
    private readonly ITeamSubscriptionsLookupService _teamSubscriptionsLookupService;
    private readonly IDashboardSummaryRepository _dashboardSummaryRepository;
    private readonly ILogger<SummaryGenerationService> _logger;
    private readonly IReportSnapshotStore _reportSnapshots;

    public SummaryGenerationService(
        IAnalysisSnapshotService analysisSnapshotService,
        IRecommendationStatusRepository recommendationStatusRepository,
        ICostAnomalyReportRepository costAnomalyReportRepository,
        IOwnershipRepository ownershipRepository,
        ITeamSubscriptionsLookupService teamSubscriptionsLookupService,
        IDashboardSummaryRepository dashboardSummaryRepository,
        ILogger<SummaryGenerationService> logger,
        IReportSnapshotStore reportSnapshots)
    {
        _analysisSnapshotService = analysisSnapshotService;
        _recommendationStatusRepository = recommendationStatusRepository;
        _costAnomalyReportRepository = costAnomalyReportRepository;
        _ownershipRepository = ownershipRepository;
        _teamSubscriptionsLookupService = teamSubscriptionsLookupService;
        _dashboardSummaryRepository = dashboardSummaryRepository;
        _logger = logger;
        _reportSnapshots = reportSnapshots;
    }

    public async Task GenerateDailySummariesAsync(DateTime dateUtc, CancellationToken cancellationToken = default)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var targetDate = dateUtc.Date;
        var dateText = targetDate.ToString("yyyy-MM-dd");
        var periodFrom = new DateTime(targetDate.Year, targetDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodTo = periodFrom.AddMonths(1).AddDays(-1);
        var periodId = periodFrom.ToString("yyyy-MM");

        var loadStopwatch = Stopwatch.StartNew();
        var recommendations = await _analysisSnapshotService.GetDailyAnalysisAsync(targetDate);
        var statusByKey = await LoadStatusByRecommendationAsync(recommendations, cancellationToken);
        var ownershipDocuments = await _ownershipRepository.GetAllAsync(cancellationToken);
        var teamBySubscription = await _teamSubscriptionsLookupService.GetAllSubscriptionTeamMappingsAsync();
        var reports = await _costAnomalyReportRepository.LoadReportsAsync(targetDate, 1, cancellationToken);
        loadStopwatch.Stop();

        // Never relabel a newer/older report as the requested day's snapshot.
        var anomalyReport = reports
            .Where(r => (string.IsNullOrWhiteSpace(r.RequestedDate) ? r.Date : r.RequestedDate) == dateText)
            .OrderByDescending(r => r.GeneratedAt)
            .FirstOrDefault();

        var ownershipMap = BuildOwnershipMap(ownershipDocuments);

        var recommendationViews = recommendations.Select(rec =>
        {
            var key = RecommendationStatusKeyBuilder.Build(rec.ResourceId, rec.Type);
            statusByKey.TryGetValue(key, out var statusDocument);
            var effectiveStatus = ResolveEffectiveStatus(statusDocument, DateTime.UtcNow);
            var teamId = ResolveTeamId(rec, ownershipMap, teamBySubscription);
            var costAmount = rec.CostAmount ?? (rec.EstimatedMonthlyCost > 0 ? rec.EstimatedMonthlyCost : 0m);
            return new RecommendationView
            {
                Recommendation = rec,
                EffectiveStatus = effectiveStatus,
                TeamId = teamId,
                CostAmount = costAmount,
                IsSql = IsSql(rec.ResourceType),
                IsCosmos = IsCosmos(rec.ResourceType)
            };
        }).ToList();

        var anomalyViews = (anomalyReport?.Subscriptions ?? new List<SubscriptionCostAnomaly>())
            .Select(item =>
            {
                teamBySubscription.TryGetValue(item.SubscriptionId, out var mappedTeamId);
                return new AnomalyView
                {
                    SubscriptionId = item.SubscriptionId,
                    TeamId = mappedTeamId,
                    HasAnomaly = item.HasAnomaly,
                    Severity = item.Severity,
                    MonthlyProjection = item.MonthlyProjection,
                    CurrentCycleCost = item.CurrentCycleCost,
                    Subscription = item
                };
            })
            .ToList();

        // A subscription without recommendations must still participate in financial totals.
        // Conversely, a known subscription missing from the report is missing, not zero.
        foreach (var subscriptionId in recommendations.Select(r => r.SubscriptionId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (anomalyViews.Any(a => a.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase))) continue;
            teamBySubscription.TryGetValue(subscriptionId, out var teamId);
            anomalyViews.Add(new AnomalyView
            {
                SubscriptionId = subscriptionId,
                TeamId = teamId,
                Subscription = new SubscriptionCostAnomaly { SubscriptionId = subscriptionId }
            });
        }

        var docs = new List<DashboardSummaryDocument>();
        docs.Add(BuildGlobalSummary(dateText, periodId, periodFrom, periodTo, recommendationViews, anomalyViews));
        docs.Add(BuildRecommendationSummary(dateText, periodId, periodFrom, periodTo, recommendationViews));
        docs.Add(BuildAnomalySummary(dateText, periodId, periodFrom, periodTo, anomalyViews));
        docs.Add(BuildDatabaseSummary(dateText, periodId, periodFrom, periodTo, recommendationViews));
        docs.AddRange(BuildTeamSummaries(dateText, periodId, periodFrom, periodTo, recommendationViews, anomalyViews));
        docs.AddRange(BuildSubscriptionSummaries(dateText, periodId, periodFrom, periodTo, recommendationViews, anomalyViews));

        foreach (var document in docs.Where(d => d.Type is DashboardSummaryTypes.GlobalDailySummary
                     or DashboardSummaryTypes.AnomalySummary or DashboardSummaryTypes.TeamDailySummary
                     or DashboardSummaryTypes.SubscriptionDailySummary))
        {
            var scope = anomalyViews.Where(a =>
                (document.TeamId == null || string.Equals(NormalizeTeam(a.TeamId), document.TeamId, StringComparison.OrdinalIgnoreCase))
                && (document.SubscriptionId == null || a.SubscriptionId.Equals(document.SubscriptionId, StringComparison.OrdinalIgnoreCase)))
                .Select(a => a.Subscription).ToList();
            var expected = document.TeamId == null && document.SubscriptionId == null
                ? Math.Max(scope.Count, anomalyReport?.TotalSubscriptionsAnalyzed ?? 0)
                : scope.Count;
            document.FinancialSummary = BuildFinancialSummary(anomalyReport, scope, expected);
            // Legacy persisted fields remain numeric; HTTP consumers use the nullable contract.
            document.TotalCost = document.FinancialSummary.TotalCost ?? 0m;
            document.MonthlyProjection = document.FinancialSummary.MonthlyProjection ?? 0m;
            document.PeriodId = $"billing-cycles:{dateText}";
            var starts = scope.Select(s => s.CurrentCycleStartDate).Distinct().ToList();
            var turns = scope.Select(s => s.CurrentCycleTurnDate).Distinct().ToList();
            document.PeriodFrom = starts.Count == 1 ? starts[0] : string.Empty;
            document.PeriodTo = turns.Count == 1 && DateTime.TryParse(turns[0], out var turn)
                ? turn.AddDays(-1).ToString("yyyy-MM-dd") : string.Empty;
        }

        var globalCurrency = docs.First(document => document.Type == DashboardSummaryTypes.GlobalDailySummary)
            .FinancialSummary?.Currency;
        foreach (var document in docs.Where(document => document.Type is DashboardSummaryTypes.GlobalDailySummary
                     or DashboardSummaryTypes.RecommendationSummary or DashboardSummaryTypes.DatabaseSummary
                     or DashboardSummaryTypes.TeamDailySummary or DashboardSummaryTypes.SubscriptionDailySummary))
        {
            var scopedRecommendations = recommendationViews.Where(view =>
                (document.TeamId == null || NormalizeTeam(view.TeamId).Equals(document.TeamId, StringComparison.OrdinalIgnoreCase))
                && (document.SubscriptionId == null || view.Recommendation.SubscriptionId.Equals(document.SubscriptionId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var currency = document.FinancialSummary?.Currency ?? globalCurrency;
            if (document.Type != DashboardSummaryTypes.DatabaseSummary)
            {
                ApplySavings(document, scopedRecommendations, currency);
            }
            if (document.Type != DashboardSummaryTypes.RecommendationSummary)
            {
                ApplyObservedCosts(document, scopedRecommendations, currency);
            }
        }

        var revision = Guid.NewGuid().ToString("N");
        var generatedAt = DateTime.UtcNow;
        var reportSnapshots = new Dictionary<string, RecommendationReport>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in docs)
        {
            document.SnapshotRevision = revision;
            document.UpdatedAt = generatedAt;
            if (document.Type is not (DashboardSummaryTypes.GlobalDailySummary or DashboardSummaryTypes.TeamDailySummary
                or DashboardSummaryTypes.SubscriptionDailySummary)) continue;
            var items = recommendationViews.Where(r =>
                (document.TeamId == null || NormalizeTeam(r.TeamId).Equals(document.TeamId, StringComparison.OrdinalIgnoreCase))
                && (document.SubscriptionId == null || r.Recommendation.SubscriptionId.Equals(document.SubscriptionId, StringComparison.OrdinalIgnoreCase)))
                .Select(r => (r.Recommendation, r.EffectiveStatus, ActiveStatuses.Contains(r.EffectiveStatus))).ToList();
            reportSnapshots[document.ScopeKey] = RecommendationReportService.BuildSnapshot(document, items);
        }
        // Persist the immutable, potentially large report in Blob before publishing its summary pointer.
        await _reportSnapshots.SaveAsync(revision, reportSnapshots, cancellationToken);
        var saveStopwatch = Stopwatch.StartNew();
        await _dashboardSummaryRepository.UpsertManyAsync(docs, cancellationToken);
        saveStopwatch.Stop();
        totalStopwatch.Stop();

        _logger.LogInformation(
            "[SUMMARY-GEN] date={date} docs={docs} loadMs={loadMs} saveMs={saveMs} totalMs={totalMs}",
            dateText,
            docs.Count,
            loadStopwatch.ElapsedMilliseconds,
            saveStopwatch.ElapsedMilliseconds,
            totalStopwatch.ElapsedMilliseconds);
    }

    private static DashboardSummaryDocument BuildGlobalSummary(
        string date,
        string periodId,
        DateTime periodFrom,
        DateTime periodTo,
        List<RecommendationView> recommendations,
        List<AnomalyView> anomalies)
    {
        var activeRecommendations = recommendations.Where(r => ActiveStatuses.Contains(r.EffectiveStatus)).ToList();
        var resolvedRecommendations = recommendations.Where(r => r.EffectiveStatus.Equals(RecommendationStatuses.Resolved, StringComparison.OrdinalIgnoreCase)).ToList();
        var falsePositiveRecommendations = recommendations.Where(r => r.EffectiveStatus.Equals(RecommendationStatuses.FalsePositive, StringComparison.OrdinalIgnoreCase)).ToList();
        var dbRecommendations = recommendations.Where(r => r.IsSql || r.IsCosmos).ToList();
        var sqlRecommendations = dbRecommendations.Where(r => r.IsSql).ToList();
        var cosmosRecommendations = dbRecommendations.Where(r => r.IsCosmos).ToList();

        var monthlyProjection = anomalies.Sum(a => a.MonthlyProjection);
        var totalCost = anomalies.Sum(a => a.CurrentCycleCost);

        return new DashboardSummaryDocument
        {
            Id = $"{DashboardSummaryTypes.GlobalDailySummary}:{date}",
            Type = DashboardSummaryTypes.GlobalDailySummary,
            ScopeKey = "global",
            Date = date,
            PeriodId = periodId,
            PeriodFrom = periodFrom.ToString("yyyy-MM-dd"),
            PeriodTo = periodTo.ToString("yyyy-MM-dd"),
            TotalCost = totalCost,
            MonthlyProjection = monthlyProjection,
            ActiveRecommendations = activeRecommendations.Count,
            PotentialMonthlySavings = RecommendationCostAccounting.Savings(activeRecommendations.Select(r => r.Recommendation)),
            PotentialAnnualSavings = RecommendationCostAccounting.Savings(activeRecommendations.Select(r => r.Recommendation)) * 12,
            ResolvedMonthlySavings = RecommendationCostAccounting.Savings(resolvedRecommendations.Select(r => r.Recommendation)),
            FalsePositiveMonthlySavings = RecommendationCostAccounting.Savings(falsePositiveRecommendations.Select(r => r.Recommendation)),
            AnomalyCount = anomalies.Count(a => a.HasAnomaly),
            CriticalAnomalyCount = anomalies.Count(a => a.HasAnomaly && a.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)),
            ResourcesWithoutOwner = recommendations.Count(r => string.IsNullOrWhiteSpace(r.TeamId)),
            DatabaseCostTotal = RecommendationCostAccounting.ObservedTotal(dbRecommendations.Select(r => r.Recommendation)),
            SqlCostTotal = RecommendationCostAccounting.ObservedTotal(sqlRecommendations.Select(r => r.Recommendation)),
            CosmosCostTotal = RecommendationCostAccounting.ObservedTotal(cosmosRecommendations.Select(r => r.Recommendation)),
            ActiveSqlRecommendations = sqlRecommendations.Count(r => ActiveStatuses.Contains(r.EffectiveStatus)),
            ActiveCosmosRecommendations = cosmosRecommendations.Count(r => ActiveStatuses.Contains(r.EffectiveStatus)),
            LowConfidenceCostCount = dbRecommendations.Count(r => string.Equals(r.Recommendation.CostMatchConfidence, CostMatchConfidenceLevels.Low, StringComparison.OrdinalIgnoreCase)),
            NotMatchedCostCount = dbRecommendations.Count(r =>
                string.Equals(r.Recommendation.CostSource, CostMatchSources.NotMatched, StringComparison.OrdinalIgnoreCase)),
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static DashboardSummaryDocument BuildRecommendationSummary(
        string date,
        string periodId,
        DateTime periodFrom,
        DateTime periodTo,
        List<RecommendationView> recommendations)
    {
        var statusCounts = recommendations
            .GroupBy(r => r.EffectiveStatus, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return new DashboardSummaryDocument
        {
            Id = $"{DashboardSummaryTypes.RecommendationSummary}:{date}",
            Type = DashboardSummaryTypes.RecommendationSummary,
            ScopeKey = "recommendations",
            Date = date,
            PeriodId = periodId,
            PeriodFrom = periodFrom.ToString("yyyy-MM-dd"),
            PeriodTo = periodTo.ToString("yyyy-MM-dd"),
            ActiveRecommendations = recommendations.Count(r => ActiveStatuses.Contains(r.EffectiveStatus)),
            PotentialMonthlySavings = RecommendationCostAccounting.Savings(recommendations.Where(r => ActiveStatuses.Contains(r.EffectiveStatus)).Select(r => r.Recommendation)),
            PotentialAnnualSavings = RecommendationCostAccounting.Savings(recommendations.Where(r => ActiveStatuses.Contains(r.EffectiveStatus)).Select(r => r.Recommendation)) * 12,
            ResolvedMonthlySavings = RecommendationCostAccounting.Savings(recommendations.Where(r => r.EffectiveStatus.Equals(RecommendationStatuses.Resolved, StringComparison.OrdinalIgnoreCase)).Select(r => r.Recommendation)),
            FalsePositiveMonthlySavings = RecommendationCostAccounting.Savings(recommendations.Where(r => r.EffectiveStatus.Equals(RecommendationStatuses.FalsePositive, StringComparison.OrdinalIgnoreCase)).Select(r => r.Recommendation)),
            StatusCounts = statusCounts,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static DashboardSummaryDocument BuildAnomalySummary(
        string date,
        string periodId,
        DateTime periodFrom,
        DateTime periodTo,
        List<AnomalyView> anomalies)
    {
        return new DashboardSummaryDocument
        {
            Id = $"{DashboardSummaryTypes.AnomalySummary}:{date}",
            Type = DashboardSummaryTypes.AnomalySummary,
            ScopeKey = "anomalies",
            Date = date,
            PeriodId = periodId,
            PeriodFrom = periodFrom.ToString("yyyy-MM-dd"),
            PeriodTo = periodTo.ToString("yyyy-MM-dd"),
            TotalCost = anomalies.Sum(a => a.CurrentCycleCost),
            MonthlyProjection = anomalies.Sum(a => a.MonthlyProjection),
            AnomalyCount = anomalies.Count(a => a.HasAnomaly),
            CriticalAnomalyCount = anomalies.Count(a => a.HasAnomaly && a.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)),
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static DashboardSummaryDocument BuildDatabaseSummary(
        string date,
        string periodId,
        DateTime periodFrom,
        DateTime periodTo,
        List<RecommendationView> recommendations)
    {
        var databaseRecommendations = recommendations.Where(r => r.IsSql || r.IsCosmos).ToList();
        var sqlRecommendations = databaseRecommendations.Where(r => r.IsSql).ToList();
        var cosmosRecommendations = databaseRecommendations.Where(r => r.IsCosmos).ToList();

        return new DashboardSummaryDocument
        {
            Id = $"{DashboardSummaryTypes.DatabaseSummary}:{date}",
            Type = DashboardSummaryTypes.DatabaseSummary,
            ScopeKey = "databases",
            Date = date,
            PeriodId = periodId,
            PeriodFrom = periodFrom.ToString("yyyy-MM-dd"),
            PeriodTo = periodTo.ToString("yyyy-MM-dd"),
            DatabaseCostTotal = RecommendationCostAccounting.ObservedTotal(databaseRecommendations.Select(r => r.Recommendation)),
            SqlCostTotal = RecommendationCostAccounting.ObservedTotal(sqlRecommendations.Select(r => r.Recommendation)),
            CosmosCostTotal = RecommendationCostAccounting.ObservedTotal(cosmosRecommendations.Select(r => r.Recommendation)),
            ActiveSqlRecommendations = sqlRecommendations.Count(r => ActiveStatuses.Contains(r.EffectiveStatus)),
            ActiveCosmosRecommendations = cosmosRecommendations.Count(r => ActiveStatuses.Contains(r.EffectiveStatus)),
            LowConfidenceCostCount = databaseRecommendations.Count(r => string.Equals(r.Recommendation.CostMatchConfidence, CostMatchConfidenceLevels.Low, StringComparison.OrdinalIgnoreCase)),
            NotMatchedCostCount = databaseRecommendations.Count(r =>
                string.Equals(r.Recommendation.CostSource, CostMatchSources.NotMatched, StringComparison.OrdinalIgnoreCase)),
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static IEnumerable<DashboardSummaryDocument> BuildTeamSummaries(
        string date,
        string periodId,
        DateTime periodFrom,
        DateTime periodTo,
        List<RecommendationView> recommendations,
        List<AnomalyView> anomalies)
    {
        var teamIds = recommendations.Select(r => NormalizeTeam(r.TeamId))
            .Concat(anomalies.Select(a => NormalizeTeam(a.TeamId))).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var teamId in teamIds)
        {
            var group = recommendations.Where(r => string.Equals(NormalizeTeam(r.TeamId), teamId, StringComparison.OrdinalIgnoreCase)).ToList();
            var anomalyGroup = anomalies.Where(a => string.Equals(NormalizeTeam(a.TeamId), teamId, StringComparison.OrdinalIgnoreCase)).ToList();
            var active = group.Count(r => ActiveStatuses.Contains(r.EffectiveStatus));
            var monthlySavings = RecommendationCostAccounting.Savings(group.Where(r => ActiveStatuses.Contains(r.EffectiveStatus)).Select(r => r.Recommendation));
            var sqlCostTotal = RecommendationCostAccounting.ObservedTotal(group.Where(r => r.IsSql).Select(r => r.Recommendation));
            var cosmosCostTotal = RecommendationCostAccounting.ObservedTotal(group.Where(r => r.IsCosmos).Select(r => r.Recommendation));
            var totalCost = anomalyGroup.Sum(a => a.CurrentCycleCost);

            yield return new DashboardSummaryDocument
            {
                Id = $"{DashboardSummaryTypes.TeamDailySummary}:{date}:{teamId}",
                Type = DashboardSummaryTypes.TeamDailySummary,
                ScopeKey = $"team:{teamId}",
                Date = date,
                PeriodId = periodId,
                PeriodFrom = periodFrom.ToString("yyyy-MM-dd"),
                PeriodTo = periodTo.ToString("yyyy-MM-dd"),
                TeamId = teamId,
                TeamName = teamId == "unassigned" ? "Sem atribuição" : teamId,
                TotalCost = totalCost,
                MonthlyProjection = anomalyGroup.Sum(a => a.MonthlyProjection),
                ActiveRecommendations = active,
                PotentialMonthlySavings = monthlySavings,
                PotentialAnnualSavings = monthlySavings * 12,
                AnomalyCount = anomalyGroup.Count(a => a.HasAnomaly),
                CriticalAnomalyCount = anomalyGroup.Count(a => a.HasAnomaly && a.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)),
                ResourcesWithoutOwner = teamId.Equals("unassigned", StringComparison.OrdinalIgnoreCase) ? group.Count() : 0,
                SqlCostTotal = sqlCostTotal,
                CosmosCostTotal = cosmosCostTotal,
                UpdatedAt = DateTime.UtcNow
            };
        }
    }

    private static IEnumerable<DashboardSummaryDocument> BuildSubscriptionSummaries(
        string date,
        string periodId,
        DateTime periodFrom,
        DateTime periodTo,
        List<RecommendationView> recommendations,
        List<AnomalyView> anomalies)
    {
        var subscriptionIds = recommendations.Select(r => r.Recommendation.SubscriptionId)
            .Concat(anomalies.Select(a => a.SubscriptionId)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var subscriptionId in subscriptionIds)
        {
            var group = recommendations.Where(r => r.Recommendation.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase)).ToList();
            var anomalyGroup = anomalies.Where(a => string.Equals(a.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase)).ToList();
            var active = group.Count(r => ActiveStatuses.Contains(r.EffectiveStatus));
            var monthlySavings = RecommendationCostAccounting.Savings(group.Where(r => ActiveStatuses.Contains(r.EffectiveStatus)).Select(r => r.Recommendation));

            yield return new DashboardSummaryDocument
            {
                Id = $"{DashboardSummaryTypes.SubscriptionDailySummary}:{date}:{subscriptionId}",
                Type = DashboardSummaryTypes.SubscriptionDailySummary,
                ScopeKey = $"subscription:{subscriptionId}",
                Date = date,
                PeriodId = periodId,
                PeriodFrom = periodFrom.ToString("yyyy-MM-dd"),
                PeriodTo = periodTo.ToString("yyyy-MM-dd"),
                SubscriptionId = subscriptionId,
                TotalCost = anomalyGroup.Sum(a => a.CurrentCycleCost),
                MonthlyProjection = anomalyGroup.Sum(a => a.MonthlyProjection),
                ActiveRecommendations = active,
                PotentialMonthlySavings = monthlySavings,
                PotentialAnnualSavings = monthlySavings * 12,
                AnomalyCount = anomalyGroup.Count(a => a.HasAnomaly),
                CriticalAnomalyCount = anomalyGroup.Count(a => a.HasAnomaly && a.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)),
                ResourcesWithoutOwner = group.Count(r => string.IsNullOrWhiteSpace(r.TeamId)),
                SqlCostTotal = RecommendationCostAccounting.ObservedTotal(group.Where(r => r.IsSql).Select(r => r.Recommendation)),
                CosmosCostTotal = RecommendationCostAccounting.ObservedTotal(group.Where(r => r.IsCosmos).Select(r => r.Recommendation)),
                UpdatedAt = DateTime.UtcNow
            };
        }
    }

    private async Task<Dictionary<string, RecommendationStatusDocument>> LoadStatusByRecommendationAsync(
        List<CostRecommendation> recommendations,
        CancellationToken cancellationToken)
    {
        var keys = recommendations
            .Select(r => RecommendationStatusKeyBuilder.Build(r.ResourceId, r.Type))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var statuses = await _recommendationStatusRepository.GetByIdsAsync(keys, cancellationToken);
        return statuses.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static void ApplySavings(
        DashboardSummaryDocument document,
        IReadOnlyCollection<RecommendationView> recommendations,
        string? currency)
    {
        var active = recommendations.Where(view => ActiveStatuses.Contains(view.EffectiveStatus));
        var resolved = recommendations.Where(view => view.EffectiveStatus.Equals(
            RecommendationStatuses.Resolved, StringComparison.OrdinalIgnoreCase));
        var falsePositive = recommendations.Where(view => view.EffectiveStatus.Equals(
            RecommendationStatuses.FalsePositive, StringComparison.OrdinalIgnoreCase));
        // The financial (anomaly) currency is only a preference: when no recommendation is quantified in it,
        // totalling in the recommendations' own currency beats reporting zero.
        currency = RecommendationCostAccounting.SavingsCurrency(
            recommendations.Select(view => view.Recommendation), currency) ?? currency;
        document.SavingsCurrency = currency;
        document.PotentialMonthlySavings = RecommendationCostAccounting.Savings(
            active.Select(view => view.Recommendation), currency);
        document.PotentialAnnualSavings = document.PotentialMonthlySavings * 12;
        document.ResolvedMonthlySavings = RecommendationCostAccounting.Savings(
            resolved.Select(view => view.Recommendation), currency);
        document.FalsePositiveMonthlySavings = RecommendationCostAccounting.Savings(
            falsePositive.Select(view => view.Recommendation), currency);
    }

    private static void ApplyObservedCosts(
        DashboardSummaryDocument document,
        IReadOnlyCollection<RecommendationView> recommendations,
        string? currency)
    {
        var sql = recommendations.Where(view => view.IsSql).Select(view => view.Recommendation);
        var cosmos = recommendations.Where(view => view.IsCosmos).Select(view => view.Recommendation);
        document.SqlCostTotal = RecommendationCostAccounting.ObservedTotal(sql, currency);
        document.CosmosCostTotal = RecommendationCostAccounting.ObservedTotal(cosmos, currency);
        document.DatabaseCostTotal = RecommendationCostAccounting.ObservedTotal(
            recommendations.Where(view => view.IsSql || view.IsCosmos).Select(view => view.Recommendation), currency);
    }

    private static string NormalizeTeam(string? teamId) => string.IsNullOrWhiteSpace(teamId) ? "unassigned" : teamId;

    private static CostReportFinancialSummary BuildFinancialSummary(
        CostAnomalyReport? report, List<SubscriptionCostAnomaly> subscriptions, int expected)
    {
        var available = subscriptions.Where(s => s.DataStatus is CostAnomalyDataStatus.Consolidated
            or CostAnomalyDataStatus.InsufficientData).ToList();
        var failed = subscriptions.Count(s => s.DataStatus == CostAnomalyDataStatus.QueryFailed);
        var missing = Math.Max(0, expected - available.Count - failed);
        var dates = available.Select(s => s.AnalysisDate)
            .Where(d => DateTime.TryParseExact(d, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
            .OrderBy(d => d, StringComparer.Ordinal).ToList();
        // No implicit currency conversion. A mixed-currency report cannot yield a scalar total.
        var currencies = available.SelectMany(s => s.DailyCosts.Select(d => d.Currency))
            .Append(report?.Currency ?? string.Empty).Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToUpperInvariant()).Distinct().ToList();
        var canSum = available.Count > 0 && currencies.Count == 1;
        return new CostReportFinancialSummary
        {
            TotalCost = canSum ? available.Sum(s => s.CurrentCycleCost) : null,
            MonthlyProjection = canSum ? available.Sum(s => s.MonthlyProjection) : null,
            Currency = currencies.Count == 1 ? currencies[0] : null,
            Availability = currencies.Count > 1 ? "MixedCurrency"
                : available.Count == 0 ? (failed > 0 ? "Failed" : "Missing")
                : failed + missing > 0 ? "Partial" : "Available",
            ReportDate = report == null ? null : string.IsNullOrWhiteSpace(report.RequestedDate) ? report.Date : report.RequestedDate,
            ReportGeneratedAt = report?.GeneratedAt,
            EarliestDataAsOf = dates.FirstOrDefault(),
            LatestDataAsOf = dates.LastOrDefault(),
            ExpectedSubscriptions = expected,
            AvailableSubscriptions = available.Count,
            FailedSubscriptions = failed,
            MissingSubscriptions = missing,
            DailyRevisions = available.SelectMany(s => s.DailyCosts.Where(d => d.Revision != null)
                .Select(d => new { Key = s.SubscriptionId.ToLowerInvariant() + "/" + d.Date, d.Revision }))
                .GroupBy(d => d.Key).ToDictionary(g => g.Key, g => g.Last().Revision!),
            Sources = available.Select(s => s.CostSource).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s).ToList()
        };
    }

    private static string ResolveEffectiveStatus(RecommendationStatusDocument? status, DateTime utcNow)
    {
        if (status == null || string.IsNullOrWhiteSpace(status.Status))
        {
            return RecommendationStatuses.New;
        }

        if (status.Status.Equals(RecommendationStatuses.Snoozed, StringComparison.OrdinalIgnoreCase)
            && status.SnoozeUntil.HasValue
            && status.SnoozeUntil.Value <= utcNow)
        {
            return RecommendationStatuses.New;
        }

        return status.Status;
    }

    private static OwnershipMap BuildOwnershipMap(IReadOnlyList<OwnershipDocument> ownershipDocuments)
    {
        var byResourceId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byResourceGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bySubscription = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in ownershipDocuments)
        {
            if (!string.IsNullOrWhiteSpace(item.ResourceId) && !string.IsNullOrWhiteSpace(item.TeamId))
            {
                byResourceId[item.ResourceId] = item.TeamId;
            }

            if (!string.IsNullOrWhiteSpace(item.SubscriptionId) &&
                !string.IsNullOrWhiteSpace(item.ResourceGroup) &&
                !string.IsNullOrWhiteSpace(item.TeamId))
            {
                byResourceGroup[$"{item.SubscriptionId}:{item.ResourceGroup}"] = item.TeamId;
            }

            if (!string.IsNullOrWhiteSpace(item.SubscriptionId) && !string.IsNullOrWhiteSpace(item.TeamId))
            {
                bySubscription[item.SubscriptionId] = item.TeamId;
            }
        }

        return new OwnershipMap(byResourceId, byResourceGroup, bySubscription);
    }

    private static string? ResolveTeamId(
        CostRecommendation recommendation,
        OwnershipMap ownershipMap,
        Dictionary<string, string> teamBySubscription)
    {
        if (ownershipMap.ByResourceId.TryGetValue(recommendation.ResourceId, out var teamFromResource))
        {
            return teamFromResource;
        }

        var resourceGroupKey = $"{recommendation.SubscriptionId}:{recommendation.ResourceGroup}";
        if (ownershipMap.ByResourceGroup.TryGetValue(resourceGroupKey, out var teamFromGroup))
        {
            return teamFromGroup;
        }

        if (ownershipMap.BySubscription.TryGetValue(recommendation.SubscriptionId, out var teamFromOwnership))
        {
            return teamFromOwnership;
        }

        return teamBySubscription.TryGetValue(recommendation.SubscriptionId, out var teamFromConfig)
            ? teamFromConfig
            : null;
    }

    private static bool IsSql(string resourceType) => DatabaseResourceClassifier.IsSql(resourceType);

    private static bool IsCosmos(string resourceType) => DatabaseResourceClassifier.IsCosmos(resourceType);

    private sealed class RecommendationView
    {
        public CostRecommendation Recommendation { get; init; } = new();
        public string EffectiveStatus { get; init; } = RecommendationStatuses.New;
        public string? TeamId { get; init; }
        public decimal CostAmount { get; init; }
        public bool IsSql { get; init; }
        public bool IsCosmos { get; init; }
    }

    private sealed class AnomalyView
    {
        public SubscriptionCostAnomaly Subscription { get; init; } = new();
        public string SubscriptionId { get; init; } = string.Empty;
        public string? TeamId { get; init; }
        public bool HasAnomaly { get; init; }
        public string Severity { get; init; } = string.Empty;
        public decimal MonthlyProjection { get; init; }
        public decimal CurrentCycleCost { get; init; }
    }

    private sealed record OwnershipMap(
        Dictionary<string, string> ByResourceId,
        Dictionary<string, string> ByResourceGroup,
        Dictionary<string, string> BySubscription);
}
