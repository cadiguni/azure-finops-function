using System.Net;
using System.Text.Json;
using Azure;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Functions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.UnitTests.Helpers;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Functions;

public class GovernanceFunctionTests
{
    private const string BudgetId = "22222222-2222-2222-2222-222222222222";
    private const string Body = """{"name":"Test","subscriptionId":"11111111-1111-1111-1111-111111111111","month":"2026-09","currency":"BRL","amount":100}""";
    private readonly Mock<IGovernanceBudgetRepository> _repository = new(MockBehavior.Strict);
    private readonly Mock<ICostExportVersionStore> _versions = new(MockBehavior.Strict);
    private readonly Mock<IGovernanceAuditLogRepository> _audit = new();
    private readonly Mock<Personal.FinOpsApi.AzureFunctions.Services.Repositories.ICostAnomalyReportRepository> _anomalyReports = new(MockBehavior.Strict);

    [Theory]
    [InlineData("list")]
    [InlineData("health")]
    [InlineData("overview")]
    [InlineData("evaluate")]
    [InlineData("save")]
    [InlineData("delete")]
    public async Task Unauthenticated_AllEndpointsReturn401WithoutAccessingData(string operation)
    {
        var response = await Invoke(Create(false, ""), operation);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.GetValues("Cache-Control").Should().Contain("no-store");
        _repository.VerifyNoOtherCalls(); _versions.VerifyNoOtherCalls(); _anomalyReports.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("list")]
    [InlineData("health")]
    [InlineData("overview")]
    [InlineData("evaluate")]
    [InlineData("save")]
    [InlineData("delete")]
    public async Task AuthenticatedWithoutRole_AllEndpointsReturn403(string operation)
    {
        var response = await Invoke(Create(true, "Other"), operation);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.GetValues("Cache-Control").Should().Contain("no-store");
        _repository.VerifyNoOtherCalls(); _versions.VerifyNoOtherCalls(); _anomalyReports.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReaderCannotReadTenantWideHealthOverview()
    {
        (await Invoke(Create(true, FinOpsRoles.Reader), "overview")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _anomalyReports.VerifyNoOtherCalls(); _versions.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("save")]
    [InlineData("delete")]
    public async Task ReaderCannotMutateBudgets(string operation)
    {
        (await Invoke(Create(true, FinOpsRoles.Reader), operation)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(FinOpsRoles.Reader)]
    [InlineData(FinOpsRoles.Admin)]
    public async Task ReadRolesCanList(string role)
    {
        _repository.Setup(r => r.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<GovernanceBudget>());
        var response = await Invoke(Create(true, role), "list");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Cache-Control").Should().Contain("no-store");
        (await HttpTestHelpers.ReadBodyAsStringAsync(response)).Should().Be("[]");
    }

    [Fact]
    public async Task AdminSave_CreatesAndReturnsCamelCase()
    {
        _repository.Setup(r => r.SaveAsync(It.IsAny<GovernanceBudget>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GovernanceBudget budget, string? _, CancellationToken _) => budget with { Version = "\"v1\"" });
        var response = await Invoke(Create(true, FinOpsRoles.Admin), "save");
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.GetValues("Cache-Control").Should().Contain("no-store");
        using var json = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));
        json.RootElement.GetProperty("definition").GetProperty("amount").GetDecimal().Should().Be(100);
        json.RootElement.GetProperty("updatedBy").GetString().Should().Be("token-oid");
        json.RootElement.TryGetProperty("Definition", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AdminDelete_UsesVersionAndReturnsSuccess()
    {
        _repository.Setup(r => r.GetAsync(BudgetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GovernanceBudget(BudgetId, new("Test", BudgetId, "2026-09", "BRL", 100), DateTimeOffset.UtcNow, "other", "\"v1\""));
        _repository.Setup(r => r.DeleteAsync(BudgetId, "\"v1\"", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        (await Invoke(Create(true, FinOpsRoles.Admin), "delete")).StatusCode.Should().Be(HttpStatusCode.OK);
        _repository.Verify(r => r.DeleteAsync(BudgetId, "\"v1\"", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VersionConflict_Returns409()
    {
        _repository.Setup(r => r.SaveAsync(It.IsAny<GovernanceBudget>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(412, "ETag mismatch"));
        var response = await Invoke(Create(true, FinOpsRoles.Admin), "save");
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Headers.GetValues("Cache-Control").Should().Contain("no-store");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"amount\":-1}")]
    public async Task InvalidInput_Returns400WithoutWriting(string body)
    {
        var req = HttpTestHelpers.CreateRequest("PUT", "https://localhost/api/governance/budgets/" + BudgetId, body);
        (await Create(true, FinOpsRoles.Admin).Save(req, BudgetId)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingBudget_Returns404()
    {
        _repository.Setup(r => r.GetAsync(BudgetId, It.IsAny<CancellationToken>())).ReturnsAsync((GovernanceBudget?)null);
        var response = await Invoke(Create(true, FinOpsRoles.Reader), "evaluate");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.GetValues("Cache-Control").Should().Contain("no-store");
    }

    [Fact]
    public async Task RepositoryUnavailable_DoesNotMasqueradeAsEmptyList()
    {
        _repository.Setup(r => r.ListAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new RequestFailedException(403, "Storage access denied"));
        var response = await Invoke(Create(true, FinOpsRoles.Reader), "list");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await HttpTestHelpers.ReadBodyAsStringAsync(response)).Should().NotContain("Storage access denied");
    }

    [Fact]
    public async Task HealthReader_ReturnsExplicitNullObservabilityFields()
    {
        const string subscription = "11111111-1111-1111-1111-111111111111";
        _versions.Setup(v => v.LoadIndexAsync(subscription, "Shadow", It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostExportRevisionIndex?)null);
        var req = HttpTestHelpers.CreateGetRequest($"https://localhost/api/governance/data-health?subscriptionId={subscription}&from=2026-09-01&to=2026-09-08");
        var response = await Create(true, FinOpsRoles.Reader).DataHealth(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));
        json.RootElement.GetProperty("historicalFailureCount").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("coveragePercent").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("status").GetString().Should().Be("unknown");
        json.RootElement.GetProperty("queueStatus").GetString().Should().Be("unavailable");
        json.RootElement.GetProperty("timerStatus").GetString().Should().Be("unavailable");
    }

    private static Task<HttpResponseData> Invoke(GovernanceFunction function, string operation)
    {
        var req = HttpTestHelpers.CreateRequest("PUT", "https://localhost/api/governance/budgets/" + BudgetId + "?version=%22v1%22", Body);
        return operation switch
        {
            "list" => function.List(req), "health" => function.DataHealth(req), "overview" => function.DataHealthOverview(req), "evaluate" => function.Evaluate(req, BudgetId),
            "save" => function.Save(req, BudgetId), _ => function.Delete(req, BudgetId)
        };
    }

    private GovernanceFunction Create(bool authenticated, string role, string userId = "token-oid")
    {
        var config = new ConfigurationBuilder().Build();
        var health = new GovernanceDataHealthService(_versions.Object, Mock.Of<ICostStorageRepository>(), config,
            NullLogger<GovernanceDataHealthService>.Instance);
        var budgets = new GovernanceBudgetService(_repository.Object, _versions.Object, health, config, NullLogger<GovernanceBudgetService>.Instance, _audit.Object);
        var user = new Mock<ICurrentUserService>();
        user.Setup(u => u.GetCurrentUserAsync(It.IsAny<HttpRequestData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentUserContext { IsAuthenticated = authenticated, Roles = new(StringComparer.OrdinalIgnoreCase) { role }, UserId = userId, UserPrincipalName = "admin@test" });
        var overview = new GovernanceDataHealthOverviewService(health, _anomalyReports.Object, config,
            NullLogger<GovernanceDataHealthOverviewService>.Instance);
        return new(health, overview, budgets, user.Object, new AuthorizationService(), NullLogger<GovernanceFunction>.Instance);
    }

    [Theory]
    [InlineData("\"amount\":100", "\"amount\":\"100\"")]
    [InlineData("\"amount\":100", "\"amount\":100,\"amount\":200")]
    [InlineData("\"amount\":100", "\"amount\":100,\"Amount\":200")]
    [InlineData("\"amount\":100", "\"amount\":100,\"teamId\":\"unimplemented\"")]
    [InlineData("\"amount\":100", "\"amount\":100,\"updatedBy\":\"forged\"")]
    public async Task StrictJson_RejectsAmbiguousNumbersDuplicatesUnknownFields(string oldValue, string newValue)
    {
        var req = HttpTestHelpers.CreateRequest("PUT", "https://localhost/api/governance/budgets/" + BudgetId, Body.Replace(oldValue, newValue));
        var response = await Create(true, FinOpsRoles.Admin).Save(req, BudgetId);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.GetValues("Cache-Control").Should().Contain("no-store");
        _repository.VerifyNoOtherCalls(); _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OversizedBodyWithoutContentLength_Is413BeforeStorage()
    {
        var req = HttpTestHelpers.CreateRequest("PUT", "https://localhost/api/governance/budgets/" + BudgetId, Body + new string(' ', 8192));
        var response = await Create(true, FinOpsRoles.Admin).Save(req, BudgetId);
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        response.Headers.GetValues("Cache-Control").Should().Contain("no-store");
        req.Body.Position.Should().Be(8193);
        _repository.VerifyNoOtherCalls(); _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PostWriteAuditFailure_ReportsAppliedAndOperationId_NotSuccess()
    {
        _repository.Setup(r => r.SaveAsync(It.IsAny<GovernanceBudget>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GovernanceBudget budget, string? _, CancellationToken _) => budget);
        _audit.SetupSequence(a => a.AppendAsync(It.IsAny<AuditLogDocument>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).ThrowsAsync(new IOException("secret storage diagnostics"));
        var response = await Invoke(Create(true, FinOpsRoles.Admin), "save");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.GetValues("Cache-Control").Should().Contain("no-store");
        var body = await HttpTestHelpers.ReadBodyAsStringAsync(response);
        body.Should().NotContain("secret storage");
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("applied").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("operationId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("save")]
    [InlineData("delete")]
    public async Task AdminWithoutTokenIdentity_CannotWriteEvenWithUpn(string operation)
    {
        (await Invoke(Create(true, FinOpsRoles.Admin, ""), operation)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _repository.VerifyNoOtherCalls(); _audit.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"name\":null}")]
    [InlineData("{\"amount\":999999999999999999999999999999999}")]
    [InlineData("{\"name\":[[[[[[[[[]]]]]]]]]}")]
    [InlineData("{\"name\":\"x\",}")]
    public async Task InvalidShapeOrDepth_Is400(string body)
    {
        var req = HttpTestHelpers.CreateRequest("PUT", "https://localhost/api/governance/budgets/" + BudgetId, body);
        (await Create(true, FinOpsRoles.Admin).Save(req, BudgetId)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _repository.VerifyNoOtherCalls(); _audit.VerifyNoOtherCalls();
    }
}