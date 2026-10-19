using System.Net;
using System.Text.Json;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Functions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Personal.FinOpsApi.AzureFunctions.UnitTests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Functions;

public class DashboardSummaryFunctionTests
{
    [Fact]
    public async Task GetSummary_WhenUserIsReader_ShouldReturnSummary()
    {
        var sut = BuildFunction(roles: [FinOpsRoles.Reader]);
        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/dashboard/summary?date=2026-07-21");

        var response = await sut.GetSummary(req);
        var body = await HttpTestHelpers.ReadBodyAsStringAsync(response);
        using var doc = JsonDocument.Parse(body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        doc.RootElement.GetProperty("sqlCostTotal").GetDecimal().Should().Be(120m);
    }

    [Fact]
    public async Task GetSummary_WhenUserIsAdmin_ShouldReturnSummary()
    {
        var sut = BuildFunction(roles: [FinOpsRoles.Admin]);
        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/dashboard/summary?date=2026-07-21");

        var response = await sut.GetSummary(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetSummary_WhenUserHasNoRole_ShouldReturnForbidden()
    {
        var sut = BuildFunction(roles: []);
        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/dashboard/summary?date=2026-07-21");

        var response = await sut.GetSummary(req);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetSummary_WhenUserIsNotAuthenticated_ShouldReturnUnauthorized()
    {
        var sut = BuildFunction(isAuthenticated: false, roles: [FinOpsRoles.Reader]);
        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/dashboard/summary?date=2026-07-21");

        var response = await sut.GetSummary(req);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetTeamsAndDatabases_WhenUserIsReader_ShouldReturnSummaries()
    {
        var sut = BuildFunction(roles: [FinOpsRoles.Reader]);

        var teamsResponse = await sut.GetTeams(HttpTestHelpers.CreateGetRequest("https://localhost/api/dashboard/teams?date=2026-07-21"));
        var teamsBody = await HttpTestHelpers.ReadBodyAsStringAsync(teamsResponse);
        using var teamsDoc = JsonDocument.Parse(teamsBody);

        var dbResponse = await sut.GetDatabases(HttpTestHelpers.CreateGetRequest("https://localhost/api/dashboard/databases?date=2026-07-21"));
        var dbBody = await HttpTestHelpers.ReadBodyAsStringAsync(dbResponse);
        using var dbDoc = JsonDocument.Parse(dbBody);

        teamsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        teamsDoc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        dbResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        dbDoc.RootElement.GetProperty("databaseCostTotal").GetDecimal().Should().Be(300m);
    }

    [Fact]
    public async Task GetSummary_ShouldSerializeMatchedZeroAndFinancialProvenance()
    {
        var sut = BuildFunction(financial: new CostReportFinancialSummary
        {
            TotalCost = 0, MonthlyProjection = 0, Currency = "BRL", Availability = "Available",
            ReportDate = "2026-07-21", AvailableSubscriptions = 1, ExpectedSubscriptions = 1
        });
        var response = await sut.GetSummary(HttpTestHelpers.CreateGetRequest("https://localhost/api/dashboard/summary?date=2026-07-21"));
        using var body = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));
        body.RootElement.GetProperty("totalCost").GetDecimal().Should().Be(0);
        body.RootElement.GetProperty("financialSummary").GetProperty("availability").GetString().Should().Be("Available");
    }

    [Fact]
    public async Task GetSummary_ShouldNotExposeLegacyUnverifiedTotalsAsObservedCost()
    {
        var sut = BuildFunction();
        var response = await sut.GetSummary(HttpTestHelpers.CreateGetRequest("https://localhost/api/dashboard/summary?date=2026-07-21"));
        using var body = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));
        body.RootElement.GetProperty("totalCost").ValueKind.Should().Be(JsonValueKind.Null);
        body.RootElement.GetProperty("financialSummary").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetSummary_WhenTodayHasNoCosts_ShouldKeepWholePreviousSnapshotAndItsDate()
    {
        var sut = BuildFunction(financial: new CostReportFinancialSummary
        {
            TotalCost = 10, Availability = "Available", ReportDate = "2026-07-21"
        }, addUnavailableNextDay: true);
        var response = await sut.GetSummary(HttpTestHelpers.CreateGetRequest("https://localhost/api/dashboard/summary?date=2026-07-22"));
        using var body = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));
        body.RootElement.GetProperty("date").GetString().Should().Be("2026-07-21");
        body.RootElement.GetProperty("requestedDate").GetString().Should().Be("2026-07-22");
        body.RootElement.GetProperty("totalCost").GetDecimal().Should().Be(10);

        var teams = await sut.GetTeams(HttpTestHelpers.CreateGetRequest("https://localhost/api/dashboard/teams?date=2026-07-22"));
        using var teamsBody = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(teams));
        teamsBody.RootElement.GetProperty("date").GetString().Should().Be("2026-07-21");
    }

    [Fact]
    public async Task GetSummary_WhenOnlyFailedSnapshotExists_ShouldReturnFailureAndNullCost()
    {
        var sut = BuildFunction(financial: new CostReportFinancialSummary { Availability = "Failed", FailedSubscriptions = 1 });
        var response = await sut.GetSummary(HttpTestHelpers.CreateGetRequest("https://localhost/api/dashboard/summary?date=2026-07-21"));
        using var body = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.RootElement.GetProperty("totalCost").ValueKind.Should().Be(JsonValueKind.Null);
        body.RootElement.GetProperty("financialSummary").GetProperty("availability").GetString().Should().Be("Failed");
    }

    private static DashboardSummaryFunction BuildFunction(bool isAuthenticated = true, string[]? roles = null,
        CostReportFinancialSummary? financial = null, bool addUnavailableNextDay = false)
    {
        roles ??= [FinOpsRoles.Reader];

        var date = new DateTime(2026, 7, 21);
        var summaries = new List<DashboardSummaryDocument>
        {
            new()
            {
                Id = $"{DashboardSummaryTypes.GlobalDailySummary}:2026-07-21",
                Type = DashboardSummaryTypes.GlobalDailySummary,
                ScopeKey = "global",
                Date = "2026-07-21",
                PeriodId = "2026-07",
                PeriodFrom = "2026-07-01",
                PeriodTo = "2026-07-31",
                TotalCost = 1500m,
                FinancialSummary = financial,
                MonthlyProjection = 1650m,
                ActiveRecommendations = 4,
                PotentialMonthlySavings = 230m,
                PotentialAnnualSavings = 2760m,
                ResolvedMonthlySavings = 100m,
                FalsePositiveMonthlySavings = 30m,
                AnomalyCount = 2,
                CriticalAnomalyCount = 1,
                ResourcesWithoutOwner = 3,
                DatabaseCostTotal = 300m,
                SqlCostTotal = 120m,
                CosmosCostTotal = 180m,
                UpdatedAt = DateTime.UtcNow
            },
            new()
            {
                Id = $"{DashboardSummaryTypes.RecommendationSummary}:2026-07-21",
                Type = DashboardSummaryTypes.RecommendationSummary,
                ScopeKey = "global",
                Date = "2026-07-21",
                StatusCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [RecommendationStatuses.Active] = 3,
                    [RecommendationStatuses.Resolved] = 1
                }
            },
            new()
            {
                Id = $"{DashboardSummaryTypes.AnomalySummary}:2026-07-21",
                Type = DashboardSummaryTypes.AnomalySummary,
                ScopeKey = "global",
                Date = "2026-07-21",
                AnomalyCount = 2,
                CriticalAnomalyCount = 1
            },
            new()
            {
                Id = $"{DashboardSummaryTypes.TeamDailySummary}:2026-07-21:team-platform",
                Type = DashboardSummaryTypes.TeamDailySummary,
                ScopeKey = "team-platform",
                Date = "2026-07-21",
                TeamId = "team-platform",
                TeamName = "Plataforma",
                TotalCost = 900m,
                MonthlyProjection = 970m,
                ActiveRecommendations = 2,
                PotentialMonthlySavings = 140m,
                PotentialAnnualSavings = 1680m,
                AnomalyCount = 1,
                CriticalAnomalyCount = 1,
                ResourcesWithoutOwner = 1,
                SqlCostTotal = 120m,
                CosmosCostTotal = 50m
            },
            new()
            {
                Id = $"{DashboardSummaryTypes.DatabaseSummary}:2026-07-21",
                Type = DashboardSummaryTypes.DatabaseSummary,
                ScopeKey = "global",
                Date = "2026-07-21",
                SqlCostTotal = 120m,
                CosmosCostTotal = 180m,
                DatabaseCostTotal = 300m,
                ActiveSqlRecommendations = 2,
                ActiveCosmosRecommendations = 1,
                LowConfidenceCostCount = 1,
                NotMatchedCostCount = 1
            }
        };

        var repo = new Mock<IDashboardSummaryRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByTypeAndDateAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string type, DateTime requested, CancellationToken _) =>
                requested == date ? summaries.FirstOrDefault(s => s.Type == type)
                : addUnavailableNextDay && requested == date.AddDays(1) && type == DashboardSummaryTypes.GlobalDailySummary
                    ? new DashboardSummaryDocument { Date = "2026-07-22", FinancialSummary = new CostReportFinancialSummary() }
                    : null);
        repo.Setup(r => r.ListByTypeAndDateAsync(DashboardSummaryTypes.TeamDailySummary, date, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summaries.Where(s => s.Type == DashboardSummaryTypes.TeamDailySummary).ToList());

        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUser.Setup(c => c.GetCurrentUserAsync(It.IsAny<Microsoft.Azure.Functions.Worker.Http.HttpRequestData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentUserContext
            {
                IsAuthenticated = isAuthenticated,
                UserPrincipalName = "user@test.local",
                Roles = roles.ToHashSet(StringComparer.OrdinalIgnoreCase)
            });

        var analysisSnapshot = new Mock<IAnalysisSnapshotService>(MockBehavior.Loose);
        var summaryGeneration = new Mock<SummaryGenerationService>(
            MockBehavior.Loose,
            Mock.Of<IAnalysisSnapshotService>(),
            Mock.Of<IRecommendationStatusRepository>(),
            Mock.Of<ICostAnomalyReportRepository>(),
            Mock.Of<IOwnershipRepository>(),
            Mock.Of<ITeamSubscriptionsLookupService>(),
            Mock.Of<IDashboardSummaryRepository>(),
            new NullLogger<SummaryGenerationService>(),
            Mock.Of<IReportSnapshotStore>());

        return new DashboardSummaryFunction(
            repo.Object,
            analysisSnapshot.Object,
            summaryGeneration.Object,
            currentUser.Object,
            new AuthorizationService(),
            new NullLogger<DashboardSummaryFunction>());
    }
}
