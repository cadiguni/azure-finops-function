using System.Net;
using System.Text.Json;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Functions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.UnitTests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Functions;

public class BillingPeriodFunctionTests
{
    [Theory]
    [InlineData(false, "", HttpStatusCode.Unauthorized)]
    [InlineData(true, "", HttpStatusCode.Forbidden)]
    [InlineData(true, FinOpsRoles.Reader, HttpStatusCode.Forbidden)]
    public async Task ListOverrides_WhenUserCannotEdit_ShouldDenyAccess(
        bool isAuthenticated, string role, HttpStatusCode expectedStatus)
    {
        var sut = BuildFunction(isAuthenticated, role);
        var response = await sut.ListOverrides(
            HttpTestHelpers.CreateGetRequest("https://localhost/api/billing/periods/overrides"));

        response.StatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task ListOverrides_WhenUserIsAdmin_ShouldReturnOverrides()
    {
        var sut = BuildFunction(FinOpsRoles.Admin);
        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/billing/periods/overrides");

        var response = await sut.ListOverrides(req);
        var body = await HttpTestHelpers.ReadBodyAsStringAsync(response);
        using var doc = JsonDocument.Parse(body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task UpsertOverride_WhenUserIsReader_ShouldReturnForbidden()
    {
        var sut = BuildFunction(FinOpsRoles.Reader);
        var req = HttpTestHelpers.CreateRequest(
            "PUT",
            "https://localhost/api/billing/periods/overrides/sub-1",
            """{"billingCycleStartDay":15}""");

        var response = await sut.UpsertOverride(req, "sub-1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteOverride_WhenUserIsAdmin_ShouldReturnOk()
    {
        var sut = BuildFunction(FinOpsRoles.Admin);
        var req = HttpTestHelpers.CreateRequest(
            "DELETE",
            "https://localhost/api/billing/periods/overrides/sub-1");

        var response = await sut.DeleteOverride(req, "sub-1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static BillingPeriodFunction BuildFunction(params string[] roles)
        => BuildFunction(true, roles);

    private static BillingPeriodFunction BuildFunction(bool isAuthenticated, params string[] roles)
    {
        var resolver = new Mock<IBillingPeriodResolver>(MockBehavior.Strict);
        resolver.Setup(r => r.ListOverridesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BillingPeriodOverrideInfo>
            {
                new()
                {
                    SubscriptionId = "sub-1",
                    BillingCycleStartDay = 15,
                    UpdatedAt = DateTime.UtcNow,
                    UpdatedBy = "admin@test.local"
                }
            });
        resolver.Setup(r => r.UpsertOverrideAsync("sub-1", 15, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingPeriodOverrideInfo
            {
                SubscriptionId = "sub-1",
                BillingCycleStartDay = 15,
                UpdatedAt = DateTime.UtcNow,
                UpdatedBy = "admin@test.local"
            });
        resolver.Setup(r => r.DeleteOverrideAsync("sub-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUser.Setup(c => c.GetCurrentUserAsync(It.IsAny<Microsoft.Azure.Functions.Worker.Http.HttpRequestData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentUserContext
            {
                IsAuthenticated = isAuthenticated,
                UserPrincipalName = "user@test.local",
                Roles = roles.ToHashSet(StringComparer.OrdinalIgnoreCase)
            });

        return new BillingPeriodFunction(
            resolver.Object,
            currentUser.Object,
            new AuthorizationService(),
            new NullLogger<BillingPeriodFunction>());
    }
}
