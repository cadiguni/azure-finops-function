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

public class FrontendApiFunctionTests
{
    [Fact]
    public async Task GetRecommendations_WhenSnoozeExpired_ShouldReturnEffectiveNew_AndNotPersistOnGet()
    {
        var recommendation = BuildRecommendation();
        var key = RecommendationStatusKeyBuilder.Build(recommendation.ResourceId, recommendation.Type);
        var expiredSnooze = new RecommendationStatusDocument
        {
            Id = key,
            Type = "recommendationStatus",
            ResourceId = recommendation.ResourceId,
            SubscriptionId = recommendation.SubscriptionId,
            ResourceGroup = recommendation.ResourceGroup,
            RecommendationType = recommendation.Type,
            Status = RecommendationStatuses.Snoozed,
            Reason = "Aguardando janela de manutenção",
            SnoozeUntil = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-2),
            UpdatedBy = "admin@test.local",
            CreatedAt = DateTime.UtcNow.AddDays(-5)
        };

        var storage = new Mock<IAnalysisSnapshotService>(MockBehavior.Strict);
        storage.Setup(s => s.GetDailyAnalysisAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<CostRecommendation> { recommendation });

        var teamLookup = new Mock<ITeamSubscriptionsLookupService>(MockBehavior.Strict);
        teamLookup.Setup(t => t.GetAllSubscriptionTeamMappingsAsync())
            .ReturnsAsync(new Dictionary<string, string>());

        var statusRepo = new Mock<IRecommendationStatusRepository>(MockBehavior.Strict);
        statusRepo.Setup(s => s.GetByIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, RecommendationStatusDocument>(StringComparer.OrdinalIgnoreCase)
            {
                [key] = expiredSnooze
            });

        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);

        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUser.Setup(c => c.GetCurrentUserAsync(It.IsAny<Microsoft.Azure.Functions.Worker.Http.HttpRequestData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentUserContext
            {
                IsAuthenticated = true,
                UserPrincipalName = "reader@test.local",
                Roles = new HashSet<string>(new[] { FinOpsRoles.Reader }, StringComparer.OrdinalIgnoreCase)
            });

        var sut = new FrontendApiFunction(
            storage.Object,
            teamLookup.Object,
            statusRepo.Object,
            auditRepo.Object,
            currentUser.Object,
            new AuthorizationService(),
            new NullLogger<FrontendApiFunction>());

        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/recommendations?date=2026-06-18");
        var response = await sut.GetRecommendations(req);
        var body = await HttpTestHelpers.ReadBodyAsStringAsync(response);
        using var doc = JsonDocument.Parse(body);
        var item = doc.RootElement.GetProperty("recommendations").EnumerateArray().Single();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        item.GetProperty("status").GetString().Should().Be(RecommendationStatuses.New);
        item.GetProperty("statusReason").ValueKind.Should().Be(JsonValueKind.Null);

        statusRepo.Verify(s => s.UpsertAsync(It.IsAny<RecommendationStatusDocument>(), It.IsAny<CancellationToken>()), Times.Never);
        auditRepo.Verify(a => a.AppendAsync(It.IsAny<AuditLogDocument>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetRecommendations_WhenSnoozeIsActive_ShouldReturnSnoozed_AndNotPersistOnGet()
    {
        var recommendation = BuildRecommendation();
        var key = RecommendationStatusKeyBuilder.Build(recommendation.ResourceId, recommendation.Type);
        var activeSnooze = new RecommendationStatusDocument
        {
            Id = key,
            Type = "recommendationStatus",
            ResourceId = recommendation.ResourceId,
            SubscriptionId = recommendation.SubscriptionId,
            ResourceGroup = recommendation.ResourceGroup,
            RecommendationType = recommendation.Type,
            Status = RecommendationStatuses.Snoozed,
            Reason = "Aguardando orçamento do próximo mês",
            SnoozeUntil = DateTime.UtcNow.AddDays(15),
            UpdatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedBy = "admin@test.local",
            CreatedAt = DateTime.UtcNow.AddDays(-10)
        };

        var storage = new Mock<IAnalysisSnapshotService>(MockBehavior.Strict);
        storage.Setup(s => s.GetDailyAnalysisAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<CostRecommendation> { recommendation });

        var teamLookup = new Mock<ITeamSubscriptionsLookupService>(MockBehavior.Strict);
        teamLookup.Setup(t => t.GetAllSubscriptionTeamMappingsAsync())
            .ReturnsAsync(new Dictionary<string, string>());

        var statusRepo = new Mock<IRecommendationStatusRepository>(MockBehavior.Strict);
        statusRepo.Setup(s => s.GetByIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, RecommendationStatusDocument>(StringComparer.OrdinalIgnoreCase)
            {
                [key] = activeSnooze
            });

        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);

        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUser.Setup(c => c.GetCurrentUserAsync(It.IsAny<Microsoft.Azure.Functions.Worker.Http.HttpRequestData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentUserContext
            {
                IsAuthenticated = true,
                UserPrincipalName = "reader@test.local",
                Roles = new HashSet<string>(new[] { FinOpsRoles.Reader }, StringComparer.OrdinalIgnoreCase)
            });

        var sut = new FrontendApiFunction(
            storage.Object,
            teamLookup.Object,
            statusRepo.Object,
            auditRepo.Object,
            currentUser.Object,
            new AuthorizationService(),
            new NullLogger<FrontendApiFunction>());

        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/recommendations?date=2026-06-18");
        var response = await sut.GetRecommendations(req);
        var body = await HttpTestHelpers.ReadBodyAsStringAsync(response);
        using var doc = JsonDocument.Parse(body);
        var item = doc.RootElement.GetProperty("recommendations").EnumerateArray().Single();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        item.GetProperty("status").GetString().Should().Be(RecommendationStatuses.Snoozed);
        item.GetProperty("statusReason").GetString().Should().Be("Aguardando orçamento do próximo mês");

        statusRepo.Verify(s => s.UpsertAsync(It.IsAny<RecommendationStatusDocument>(), It.IsAny<CancellationToken>()), Times.Never);
        auditRepo.Verify(a => a.AppendAsync(It.IsAny<AuditLogDocument>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetRecommendations_ShouldReturnOccurrenceAndLastStatusFields()
    {
        var recommendation = BuildRecommendation();
        var key = RecommendationStatusKeyBuilder.Build(recommendation.ResourceId, recommendation.Type);
        var status = new RecommendationStatusDocument
        {
            Id = key,
            ResourceId = recommendation.ResourceId,
            SubscriptionId = recommendation.SubscriptionId,
            ResourceGroup = recommendation.ResourceGroup,
            RecommendationType = recommendation.Type,
            Status = RecommendationStatuses.InReview,
            Reason = "Investigando",
            UpdatedAt = DateTime.UtcNow.AddHours(-2),
            UpdatedBy = "admin@test.local"
        };

        var storage = new Mock<IAnalysisSnapshotService>(MockBehavior.Strict);
        storage.Setup(s => s.GetDailyAnalysisAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<CostRecommendation> { recommendation });

        var teamLookup = new Mock<ITeamSubscriptionsLookupService>(MockBehavior.Strict);
        teamLookup.Setup(t => t.GetAllSubscriptionTeamMappingsAsync())
            .ReturnsAsync(new Dictionary<string, string>());

        var statusRepo = new Mock<IRecommendationStatusRepository>(MockBehavior.Strict);
        statusRepo.Setup(s => s.GetByIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, RecommendationStatusDocument>(StringComparer.OrdinalIgnoreCase)
            {
                [key] = status
            });

        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUser.Setup(c => c.GetCurrentUserAsync(It.IsAny<Microsoft.Azure.Functions.Worker.Http.HttpRequestData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentUserContext
            {
                IsAuthenticated = true,
                UserPrincipalName = "reader@test.local",
                Roles = new HashSet<string>(new[] { FinOpsRoles.Reader }, StringComparer.OrdinalIgnoreCase)
            });

        var sut = new FrontendApiFunction(
            storage.Object,
            teamLookup.Object,
            statusRepo.Object,
            auditRepo.Object,
            currentUser.Object,
            new AuthorizationService(),
            new NullLogger<FrontendApiFunction>());

        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/recommendations?date=2026-06-18&historyDays=1");
        var response = await sut.GetRecommendations(req);
        var body = await HttpTestHelpers.ReadBodyAsStringAsync(response);
        using var doc = JsonDocument.Parse(body);
        var item = doc.RootElement.GetProperty("recommendations").EnumerateArray().Single();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        item.GetProperty("id").GetString().Should().Be(key);
        item.GetProperty("occurrenceCount").GetInt32().Should().Be(1);
        item.GetProperty("lastStatusUpdatedBy").GetString().Should().Be("admin@test.local");
        item.GetProperty("lastStatusUpdatedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetRecommendations_WithVersionedStores_ShouldUseCanonicalSnapshotInsteadOfLegacyAnalysis()
    {
        var date = new DateTime(2026, 8, 31);
        var revision = "snapshot-canonical";
        var summary = new DashboardSummaryDocument
        {
            Type = DashboardSummaryTypes.GlobalDailySummary,
            ScopeKey = "global",
            Date = "2026-08-31",
            SnapshotRevision = revision,
            PeriodFrom = "2026-08-01",
            PeriodTo = "2026-08-31",
            FinancialSummary = new CostReportFinancialSummary
            {
                TotalCost = 500m,
                Currency = "BRL",
                Availability = "Available",
                Sources = [CostDataSources.CostManagementExport]
            }
        };
        var report = new RecommendationReport
        {
            AnalysisDate = date,
            SnapshotRevision = revision,
            PeriodFrom = summary.PeriodFrom,
            PeriodTo = summary.PeriodTo,
            FinancialSummary = summary.FinancialSummary,
            ManagementGroups =
            {
                new ManagementGroupReport
                {
                    Id = "mg",
                    Subscriptions =
                    {
                        new SubscriptionReport
                        {
                            Id = "sub-canonical",
                            ResourceGroups =
                            {
                                new ResourceGroupReport
                                {
                                    Name = "rg",
                                    Recommendations =
                                    {
                                        new ActionableRecommendation
                                        {
                                            RecommendationType = "SqlDatabaseCostReview",
                                            ResourceId = "/subscriptions/sub-canonical/resourceGroups/rg/providers/Microsoft.Sql/servers/s/databases/db",
                                            ResourceName = "db",
                                            ResourceType = "Microsoft.Sql/servers/databases",
                                            Description = "Investigar métricas",
                                            RecommendationText = "Validar utilização",
                                            Priority = "Medium",
                                            Confidence = "90%",
                                            PotentialSavings = 25m,
                                            CurrentCost = 120m,
                                            ObservedCost = 100m,
                                            Currency = "BRL",
                                            CostSource = CostMatchSources.ResourceId
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
        var summaries = new Mock<IDashboardSummaryRepository>(MockBehavior.Strict);
        summaries.Setup(repository => repository.GetByTypeAndDateAsync(
                DashboardSummaryTypes.GlobalDailySummary, date, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);
        var snapshots = new Mock<IReportSnapshotStore>(MockBehavior.Strict);
        snapshots.Setup(store => store.LoadAsync(revision, "global", It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);
        var storage = new Mock<IAnalysisSnapshotService>(MockBehavior.Strict);
        var teams = new Mock<ITeamSubscriptionsLookupService>(MockBehavior.Strict);
        teams.Setup(service => service.GetAllSubscriptionTeamMappingsAsync())
            .ReturnsAsync(new Dictionary<string, string>());
        var statuses = new Mock<IRecommendationStatusRepository>(MockBehavior.Strict);
        statuses.Setup(repository => repository.GetByIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, RecommendationStatusDocument>());
        var audit = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUser.Setup(service => service.GetCurrentUserAsync(
                It.IsAny<Microsoft.Azure.Functions.Worker.Http.HttpRequestData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentUserContext
            {
                IsAuthenticated = true,
                Roles = new HashSet<string>([FinOpsRoles.Reader], StringComparer.OrdinalIgnoreCase)
            });
        var sut = new FrontendApiFunction(
            storage.Object, teams.Object, statuses.Object, audit.Object, currentUser.Object,
            new AuthorizationService(), new NullLogger<FrontendApiFunction>(), summaries.Object, snapshots.Object);

        var response = await sut.GetRecommendations(
            HttpTestHelpers.CreateGetRequest("https://localhost/api/recommendations?date=2026-08-31"));
        using var body = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.RootElement.GetProperty("snapshotRevision").GetString().Should().Be(revision);
        body.RootElement.GetProperty("periodFrom").GetString().Should().Be("2026-08-01");
        body.RootElement.GetProperty("totalEstimatedMonthlySavings").GetDecimal().Should().Be(25m);
        body.RootElement.GetProperty("recommendations").EnumerateArray().Single()
            .GetProperty("subscriptionId").GetString().Should().Be("sub-canonical");
        storage.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetRecommendationDetails_WhenReader_ShouldReturnDetailsAndHistory()
    {
        var recommendation = BuildRecommendation();
        var key = RecommendationStatusKeyBuilder.Build(recommendation.ResourceId, recommendation.Type);
        var status = new RecommendationStatusDocument
        {
            Id = key,
            ResourceId = recommendation.ResourceId,
            SubscriptionId = recommendation.SubscriptionId,
            ResourceGroup = recommendation.ResourceGroup,
            RecommendationType = recommendation.Type,
            Status = RecommendationStatuses.Accepted,
            Reason = "Planejado para este sprint",
            UpdatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedBy = "admin@test.local"
        };

        var storage = new Mock<IAnalysisSnapshotService>(MockBehavior.Strict);
        storage.Setup(s => s.GetDailyAnalysisAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<CostRecommendation> { recommendation });

        var teamLookup = new Mock<ITeamSubscriptionsLookupService>(MockBehavior.Strict);
        teamLookup.Setup(t => t.GetAllSubscriptionTeamMappingsAsync())
            .ReturnsAsync(new Dictionary<string, string>());

        var statusRepo = new Mock<IRecommendationStatusRepository>(MockBehavior.Strict);
        statusRepo.Setup(s => s.GetByIdAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        auditRepo.Setup(a => a.GetByEntityIdAsync(key, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AuditLogDocument>
            {
                new()
                {
                    EntityId = key,
                    EntityType = "recommendationStatus",
                    Action = "StatusChanged",
                    PerformedBy = "admin@test.local",
                    PerformedAt = DateTime.UtcNow.AddMinutes(-30)
                }
            });

        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUser.Setup(c => c.GetCurrentUserAsync(It.IsAny<Microsoft.Azure.Functions.Worker.Http.HttpRequestData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentUserContext
            {
                IsAuthenticated = true,
                UserPrincipalName = "reader@test.local",
                Roles = new HashSet<string>(new[] { FinOpsRoles.Reader }, StringComparer.OrdinalIgnoreCase)
            });

        var sut = new FrontendApiFunction(
            storage.Object,
            teamLookup.Object,
            statusRepo.Object,
            auditRepo.Object,
            currentUser.Object,
            new AuthorizationService(),
            new NullLogger<FrontendApiFunction>());

        var req = HttpTestHelpers.CreateGetRequest($"https://localhost/api/recommendations/{key}?historyDays=1");
        var response = await sut.GetRecommendationDetails(req, key);
        var body = await HttpTestHelpers.ReadBodyAsStringAsync(response);
        using var doc = JsonDocument.Parse(body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        doc.RootElement.GetProperty("recommendation").GetProperty("id").GetString().Should().Be(key);
        doc.RootElement.GetProperty("recommendation").GetProperty("status").GetString().Should().Be(RecommendationStatuses.Accepted);
        doc.RootElement.GetProperty("auditLogs").EnumerateArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task GetRecommendationHistory_WhenUserWithoutRole_ShouldReturnForbidden()
    {
        var storage = new Mock<IAnalysisSnapshotService>(MockBehavior.Strict);
        var teamLookup = new Mock<ITeamSubscriptionsLookupService>(MockBehavior.Strict);
        var statusRepo = new Mock<IRecommendationStatusRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Strict);

        currentUser.Setup(c => c.GetCurrentUserAsync(It.IsAny<Microsoft.Azure.Functions.Worker.Http.HttpRequestData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentUserContext
            {
                IsAuthenticated = true,
                UserPrincipalName = "user@test.local",
                Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            });

        var sut = new FrontendApiFunction(
            storage.Object,
            teamLookup.Object,
            statusRepo.Object,
            auditRepo.Object,
            currentUser.Object,
            new AuthorizationService(),
            new NullLogger<FrontendApiFunction>());

        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/recommendations/abc/history");
        var response = await sut.GetRecommendationHistory(req, "abc");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static CostRecommendation BuildRecommendation()
    {
        return new CostRecommendation
        {
            Type = "IdleVm",
            ResourceId = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-finops/providers/Microsoft.Compute/virtualMachines/vm-finops-01",
            ResourceName = "vm-finops-01",
            ResourceType = "Microsoft.Compute/virtualMachines",
            ResourceGroup = "rg-finops",
            SubscriptionId = "00000000-0000-0000-0000-000000000001",
            Priority = "High",
            Description = "VM com baixa utilização",
            Recommendation = "Desligar ou reduzir SKU",
            EstimatedMonthlySavings = 150m,
            DailyCost = 5m,
            EstimatedMonthlyCost = 150m,
            Confidence = 0.9m,
            Impact = "High",
            LastEvaluationDate = DateTime.UtcNow.AddDays(-1)
        };
    }
}
