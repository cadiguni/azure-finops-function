using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class SummaryGenerationServiceTests
{
    [Fact]
    public async Task GenerateDailySummariesAsync_ShouldCreateGlobalAndTeamSummaries()
    {
        var date = new DateTime(2026, 7, 21);
        var recommendations = BuildRecommendations();
        var statusMap = new Dictionary<string, RecommendationStatusDocument>(StringComparer.OrdinalIgnoreCase)
        {
            [RecommendationStatusKeyBuilder.Build(recommendations[0].ResourceId, recommendations[0].Type)] = new()
            {
                Id = RecommendationStatusKeyBuilder.Build(recommendations[0].ResourceId, recommendations[0].Type),
                Status = RecommendationStatuses.Resolved
            },
            [RecommendationStatusKeyBuilder.Build(recommendations[1].ResourceId, recommendations[1].Type)] = new()
            {
                Id = RecommendationStatusKeyBuilder.Build(recommendations[1].ResourceId, recommendations[1].Type),
                Status = RecommendationStatuses.FalsePositive
            }
        };

        var analysisSnapshot = new Mock<IAnalysisSnapshotService>(MockBehavior.Strict);
        analysisSnapshot.Setup(s => s.GetDailyAnalysisAsync(date)).ReturnsAsync(recommendations);

        var statusRepo = new Mock<IRecommendationStatusRepository>(MockBehavior.Strict);
        statusRepo.Setup(s => s.GetByIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(statusMap);

        var anomaliesRepo = new Mock<ICostAnomalyReportRepository>(MockBehavior.Strict);
        anomaliesRepo.Setup(r => r.LoadReportsAsync(date, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CostAnomalyReport>
            {
                new()
                {
                    Date = date.ToString("yyyy-MM-dd"),
                    Subscriptions =
                    {
                        new SubscriptionCostAnomaly
                        {
                            SubscriptionId = "sub-001",
                            DataStatus = CostAnomalyDataStatus.Consolidated,
                            HasAnomaly = true,
                            Severity = "Critical",
                            MonthlyProjection = 1000m,
                            CurrentCycleCost = 650m
                        }
                    }
                }
            });

        var ownershipRepo = new Mock<IOwnershipRepository>(MockBehavior.Strict);
        ownershipRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OwnershipDocument>
            {
                new()
                {
                    Id = "sub-owner",
                    SubscriptionId = "sub-001",
                    TeamId = "team-platform"
                }
            });

        var teamLookup = new Mock<ITeamSubscriptionsLookupService>(MockBehavior.Strict);
        teamLookup.Setup(t => t.GetAllSubscriptionTeamMappingsAsync())
            .ReturnsAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sub-001"] = "team-platform",
                ["sub-002"] = "team-data"
            });

        List<DashboardSummaryDocument>? captured = null;
        var summaryRepo = new Mock<IDashboardSummaryRepository>(MockBehavior.Strict);
        summaryRepo.Setup(r => r.UpsertManyAsync(It.IsAny<IEnumerable<DashboardSummaryDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<DashboardSummaryDocument>, CancellationToken>((docs, _) => captured = docs.ToList())
            .Returns(Task.CompletedTask);

        var sut = new SummaryGenerationService(
            analysisSnapshot.Object,
            statusRepo.Object,
            anomaliesRepo.Object,
            ownershipRepo.Object,
            teamLookup.Object,
            summaryRepo.Object,
            new NullLogger<SummaryGenerationService>(), Mock.Of<IReportSnapshotStore>());

        await sut.GenerateDailySummariesAsync(date);

        captured.Should().NotBeNull();
        var documents = captured!;
        documents.Should().Contain(d => d.Type == DashboardSummaryTypes.GlobalDailySummary);
        documents.Should().Contain(d => d.Type == DashboardSummaryTypes.TeamDailySummary);
        documents.Should().Contain(d => d.Type == DashboardSummaryTypes.DatabaseSummary);

        var global = documents.Single(d => d.Type == DashboardSummaryTypes.GlobalDailySummary);
        global.ActiveRecommendations.Should().BeGreaterThan(0);
        global.PotentialAnnualSavings.Should().Be(global.PotentialMonthlySavings * 12);
        global.ResolvedMonthlySavings.Should().Be(0m);
        global.FalsePositiveMonthlySavings.Should().Be(90m);
        global.CriticalAnomalyCount.Should().Be(1);
        global.SqlCostTotal.Should().Be(300m);
        global.CosmosCostTotal.Should().BeGreaterThan(0);
        global.NotMatchedCostCount.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task GenerateDailySummariesAsync_ShouldBuildDatabaseConfidenceCounters()
    {
        var date = new DateTime(2026, 7, 21);
        var recommendations = BuildRecommendations();

        var analysisSnapshot = new Mock<IAnalysisSnapshotService>(MockBehavior.Strict);
        analysisSnapshot.Setup(s => s.GetDailyAnalysisAsync(date)).ReturnsAsync(recommendations);

        var statusRepo = new Mock<IRecommendationStatusRepository>(MockBehavior.Strict);
        statusRepo.Setup(s => s.GetByIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, RecommendationStatusDocument>());

        var anomaliesRepo = new Mock<ICostAnomalyReportRepository>(MockBehavior.Strict);
        anomaliesRepo.Setup(r => r.LoadReportsAsync(date, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CostAnomalyReport>());
        anomaliesRepo.Setup(r => r.LoadLatestReportAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostAnomalyReport?)null);

        var ownershipRepo = new Mock<IOwnershipRepository>(MockBehavior.Strict);
        ownershipRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<OwnershipDocument>());

        var teamLookup = new Mock<ITeamSubscriptionsLookupService>(MockBehavior.Strict);
        teamLookup.Setup(t => t.GetAllSubscriptionTeamMappingsAsync())
            .ReturnsAsync(new Dictionary<string, string>());

        List<DashboardSummaryDocument>? captured = null;
        var summaryRepo = new Mock<IDashboardSummaryRepository>(MockBehavior.Strict);
        summaryRepo.Setup(r => r.UpsertManyAsync(It.IsAny<IEnumerable<DashboardSummaryDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<DashboardSummaryDocument>, CancellationToken>((docs, _) => captured = docs.ToList())
            .Returns(Task.CompletedTask);

        var sut = new SummaryGenerationService(
            analysisSnapshot.Object,
            statusRepo.Object,
            anomaliesRepo.Object,
            ownershipRepo.Object,
            teamLookup.Object,
            summaryRepo.Object,
            new NullLogger<SummaryGenerationService>(), Mock.Of<IReportSnapshotStore>());

        await sut.GenerateDailySummariesAsync(date);

        var dbSummary = captured!.Single(d => d.Type == DashboardSummaryTypes.DatabaseSummary);
        dbSummary.LowConfidenceCostCount.Should().Be(1);
        dbSummary.NotMatchedCostCount.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-12)]
    public async Task FinancialSummary_ShouldPreserveZeroAndCredits(decimal amount)
    {
        var report = Report(Subscription("sub-001", amount));
        var docs = await Generate(report, BuildRecommendations());
        var financial = docs.Single(d => d.Type == DashboardSummaryTypes.GlobalDailySummary).FinancialSummary!;
        financial.TotalCost.Should().Be(amount);
        financial.MonthlyProjection.Should().Be(amount);
        financial.Availability.Should().Be("Partial"); // sub-002 has recommendations but no cost report.
        financial.MissingSubscriptions.Should().Be(1);
    }

    [Fact]
    public async Task FinancialSummary_ShouldIncludeSubscriptionsWithoutRecommendationsAndUnassignedCosts()
    {
        var report = Report(Subscription("sub-001", 100), Subscription("sub-002", 200), Subscription("sub-003", 30));
        var docs = await Generate(report);
        var global = docs.Single(d => d.Type == DashboardSummaryTypes.GlobalDailySummary);
        var teams = docs.Where(d => d.Type == DashboardSummaryTypes.TeamDailySummary).ToList();
        var subscriptions = docs.Where(d => d.Type == DashboardSummaryTypes.SubscriptionDailySummary).ToList();
        global.FinancialSummary!.TotalCost.Should().Be(330);
        teams.Sum(d => d.FinancialSummary!.TotalCost).Should().Be(330);
        subscriptions.Sum(d => d.FinancialSummary!.TotalCost).Should().Be(330);
        teams.Single(d => d.TeamId == "unassigned").FinancialSummary!.TotalCost.Should().Be(230);
        subscriptions.Should().HaveCount(3);
        global.FinancialSummary.Availability.Should().Be("Available");
        global.FinancialSummary.Finality.Should().Be("Provisional");
        global.FinancialSummary.ReportGeneratedAt.Should().Be(report.GeneratedAt);
    }

    [Fact]
    public async Task FinancialSummary_ShouldTrackDailyCostRevisionsUsedBySnapshot()
    {
        var subscription = Subscription("sub-001", 100);
        subscription.DailyCosts.Add(new DailyCostEntry
        {
            Date = "2026-07-20",
            TotalCost = 100m,
            Currency = "BRL",
            Revision = "revision-corrected"
        });

        var docs = await Generate(Report(subscription));

        var financial = docs.Single(d => d.Type == DashboardSummaryTypes.GlobalDailySummary).FinancialSummary!;
        financial.DailyRevisions.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>("sub-001/2026-07-20", "revision-corrected"));
    }

    [Fact]
    public async Task FinancialSummary_ShouldExcludeFailuresAndMissingDataButKeepPartialHistory()
    {
        var partial = Subscription("sub-001", 45);
        partial.DataStatus = CostAnomalyDataStatus.InsufficientData;
        var failed = Subscription("sub-002", 900);
        failed.DataStatus = CostAnomalyDataStatus.QueryFailed;
        var missing = Subscription("sub-003", 800);
        missing.DataStatus = CostAnomalyDataStatus.NoCostData;
        var report = Report(partial, failed, missing);
        report.TotalSubscriptionsAnalyzed = 4;
        var docs = await Generate(report);
        var result = docs.Single(d => d.Type == DashboardSummaryTypes.GlobalDailySummary).FinancialSummary!;
        result.TotalCost.Should().Be(45);
        result.Availability.Should().Be("Partial");
        result.AvailableSubscriptions.Should().Be(1);
        result.FailedSubscriptions.Should().Be(1);
        result.MissingSubscriptions.Should().Be(2);
    }

    [Theory]
    [InlineData(CostAnomalyDataStatus.QueryFailed, "Failed")]
    [InlineData(CostAnomalyDataStatus.NoCostData, "Missing")]
    public async Task FinancialSummary_ShouldReturnNullNotZeroWhenCostsAreUnavailable(string status, string expected)
    {
        var subscription = Subscription("sub-001", 100);
        subscription.DataStatus = status;
        var docs = await Generate(Report(subscription));
        var result = docs.Single(d => d.Type == DashboardSummaryTypes.GlobalDailySummary).FinancialSummary!;
        result.TotalCost.Should().BeNull();
        result.MonthlyProjection.Should().BeNull();
        result.Availability.Should().Be(expected);
    }

    [Fact]
    public async Task FinancialSummary_ShouldNotSubstituteRecommendationEstimatesOrLoadLatestReport()
    {
        var docs = await Generate(null, BuildRecommendations());
        var result = docs.Single(d => d.Type == DashboardSummaryTypes.GlobalDailySummary).FinancialSummary!;
        result.TotalCost.Should().BeNull();
        result.ReportDate.Should().BeNull();
        result.Availability.Should().Be("Missing");
    }

    [Theory]
    [InlineData("2026-07-20")]
    [InlineData("2026-07-22")]
    public async Task FinancialSummary_ShouldNotRelabelAnotherReportsDate(string reportDate)
    {
        var report = Report(Subscription("sub-001", 100));
        report.RequestedDate = reportDate;
        var docs = await Generate(report);
        docs.Single(d => d.Type == DashboardSummaryTypes.GlobalDailySummary).FinancialSummary!.TotalCost.Should().BeNull();
    }

    [Fact]
    public async Task FinancialSummary_ShouldExposeDifferentCoverageDatesAndNotLabelMixedCyclesAsCalendarMonth()
    {
        var older = Subscription("sub-001", 100);
        older.AnalysisDate = "2026-07-18";
        older.CurrentCycleStartDate = "2026-06-25";
        older.CurrentCycleTurnDate = "2026-07-25";
        var docs = await Generate(Report(older, Subscription("sub-002", 20)));
        var global = docs.Single(d => d.Type == DashboardSummaryTypes.GlobalDailySummary);
        global.PeriodId.Should().StartWith("billing-cycles:");
        global.PeriodFrom.Should().BeEmpty();
        global.PeriodTo.Should().BeEmpty();
        global.FinancialSummary!.EarliestDataAsOf.Should().Be("2026-07-18");
        global.FinancialSummary.LatestDataAsOf.Should().Be("2026-07-20");
        var subscription = docs.Single(d => d.SubscriptionId == "sub-001");
        subscription.PeriodFrom.Should().Be("2026-06-25");
        subscription.PeriodTo.Should().Be("2026-07-24");
    }

    [Fact]
    public async Task FinancialSummary_ShouldNotSumMixedCurrencies()
    {
        var usd = Subscription("sub-002", 20);
        usd.DailyCosts.Add(new DailyCostEntry { Date = "2026-07-20", Currency = "USD", TotalCost = 20 });
        var docs = await Generate(Report(Subscription("sub-001", 10), usd));
        var result = docs.Single(d => d.Type == DashboardSummaryTypes.GlobalDailySummary).FinancialSummary!;
        result.Availability.Should().Be("MixedCurrency");
        result.TotalCost.Should().BeNull();
        result.Currency.Should().BeNull();
    }

    [Fact]
    public async Task Summary_ShouldKeepSavingsAndObservedCostsInFinancialCurrency()
    {
        var recommendations = BuildRecommendations();
        recommendations.Add(new CostRecommendation
        {
            Type = "SqlUsd",
            ResourceId = "/subscriptions/sub-002/resourceGroups/rg/providers/Microsoft.Sql/servers/s/databases/usd",
            ResourceName = "usd",
            ResourceType = "Microsoft.Sql/servers/databases",
            ResourceGroup = "rg",
            SubscriptionId = "sub-002",
            ObservedCost = 50m,
            EstimatedMonthlyCost = 50m,
            EstimatedMonthlySavings = 20m,
            CostCurrency = "USD",
            CostSource = CostMatchSources.ResourceId
        });

        var docs = await Generate(Report(Subscription("sub-001", 100), Subscription("sub-002", 200)), recommendations);
        var global = docs.Single(document => document.Type == DashboardSummaryTypes.GlobalDailySummary);

        global.FinancialSummary!.Currency.Should().Be("BRL");
        global.PotentialMonthlySavings.Should().Be(90m);
        global.DatabaseCostTotal.Should().Be(500m);
    }

    [Fact]
    public async Task DatabaseSummary_ShouldNotClassifyMatchedZeroAsMissing()
    {
        var recommendations = BuildRecommendations().Take(1).ToList();
        recommendations[0].CostAmount = 0;
        recommendations[0].EstimatedMonthlyCost = 0;
        var docs = await Generate(null, recommendations);
        docs.Single(d => d.Type == DashboardSummaryTypes.DatabaseSummary).NotMatchedCostCount.Should().Be(0);
    }

    private static SubscriptionCostAnomaly Subscription(string id, decimal cost) => new()
    {
        SubscriptionId = id, DataStatus = CostAnomalyDataStatus.Consolidated,
        CurrentCycleCost = cost, MonthlyProjection = cost, AnalysisDate = "2026-07-20",
        CurrentCycleStartDate = "2026-07-01", CurrentCycleTurnDate = "2026-08-01",
        CostSource = CostDataSources.CostManagementExport
    };

    private static CostAnomalyReport Report(params SubscriptionCostAnomaly[] subscriptions) => new()
    {
        Date = "2026-07-21", RequestedDate = "2026-07-21", Currency = "BRL",
        GeneratedAt = new DateTime(2026, 7, 21, 10, 0, 0, DateTimeKind.Utc),
        TotalSubscriptionsAnalyzed = subscriptions.Length, Subscriptions = subscriptions.ToList()
    };

    private static async Task<List<DashboardSummaryDocument>> Generate(CostAnomalyReport? report, List<CostRecommendation>? recommendations = null)
    {
        var date = new DateTime(2026, 7, 21);
        var snapshots = new Mock<IAnalysisSnapshotService>();
        snapshots.Setup(s => s.GetDailyAnalysisAsync(date)).ReturnsAsync(recommendations ?? new());
        var statuses = new Mock<IRecommendationStatusRepository>();
        statuses.Setup(s => s.GetByIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, RecommendationStatusDocument>());
        var reports = new Mock<ICostAnomalyReportRepository>(MockBehavior.Strict);
        reports.Setup(r => r.LoadReportsAsync(date, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(report == null ? Array.Empty<CostAnomalyReport>() : new[] { report });
        var owners = new Mock<IOwnershipRepository>();
        owners.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<OwnershipDocument>());
        var teams = new Mock<ITeamSubscriptionsLookupService>();
        teams.Setup(t => t.GetAllSubscriptionTeamMappingsAsync()).ReturnsAsync(new Dictionary<string, string> { ["sub-001"] = "team-a" });
        var repository = new Mock<IDashboardSummaryRepository>();
        var documents = new List<DashboardSummaryDocument>();
        repository.Setup(r => r.UpsertManyAsync(It.IsAny<IEnumerable<DashboardSummaryDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<DashboardSummaryDocument>, CancellationToken>((docs, _) => documents.AddRange(docs))
            .Returns(Task.CompletedTask);
        var service = new SummaryGenerationService(snapshots.Object, statuses.Object, reports.Object, owners.Object,
            teams.Object, repository.Object, NullLogger<SummaryGenerationService>.Instance, Mock.Of<IReportSnapshotStore>());
        await service.GenerateDailySummariesAsync(date);
        reports.Verify(r => r.LoadLatestReportAsync(It.IsAny<CancellationToken>()), Times.Never);
        return documents;
    }

    private static List<CostRecommendation> BuildRecommendations()
    {
        return new List<CostRecommendation>
        {
            new()
            {
                Type = "SqlIdle",
                ResourceId = "/subscriptions/sub-001/resourceGroups/rg-data/providers/Microsoft.Sql/servers/sql-main/databases/db01",
                ResourceName = "db01",
                ResourceType = "Microsoft.Sql/servers/databases",
                ResourceGroup = "rg-data",
                SubscriptionId = "sub-001",
                EstimatedMonthlySavings = 120m,
                EstimatedMonthlyCost = 300m,
                ObservedCost = 300m,
                CostAmount = 300m,
                CostSource = CostMatchSources.ParentResource,
                CostMatchConfidence = CostMatchConfidenceLevels.Medium
            },
            new()
            {
                Type = "CosmosOptimize",
                ResourceId = "/subscriptions/sub-002/resourceGroups/rg-core/providers/Microsoft.DocumentDB/databaseAccounts/cosmos01",
                ResourceName = "cosmos01",
                ResourceType = "Microsoft.DocumentDB/databaseAccounts",
                ResourceGroup = "rg-core",
                SubscriptionId = "sub-002",
                EstimatedMonthlySavings = 90m,
                EstimatedMonthlyCost = 200m,
                ObservedCost = 200m,
                CostAmount = 200m,
                CostSource = CostMatchSources.ResourceId,
                CostMatchConfidence = CostMatchConfidenceLevels.High
            },
            new()
            {
                Type = "SqlUnknownCost",
                ResourceId = "/subscriptions/sub-001/resourceGroups/rg-data/providers/Microsoft.Sql/servers/sql-main/databases/db-missing",
                ResourceName = "db-missing",
                ResourceType = "Microsoft.Sql/servers/databases",
                ResourceGroup = "rg-data",
                SubscriptionId = "sub-001",
                EstimatedMonthlySavings = 35m,
                EstimatedMonthlyCost = 0m,
                CostAmount = 0m,
                CostSource = CostMatchSources.NotMatched,
                CostMatchConfidence = CostMatchConfidenceLevels.Low
            }
        };
    }
}
