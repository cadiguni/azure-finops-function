using System.Net;
using System.Text.Json;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Functions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.UnitTests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Functions;

public class CostByServiceManualFunctionTests
{
    [Fact]
    public async Task RunAsync_WhenAllWithOneFailure_ShouldReturnMultiStatus()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["COST_SUBSCRIPTIONS"] = "sub-ok,sub-fail"
        });

        var costClient = new Mock<ICostManagementClient>(MockBehavior.Strict);
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);

        costClient.Setup(c => c.QueryCostByServiceAsync(
                "sub-ok",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                "None",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostByServiceQueryResponse
            {
                SubscriptionId = "sub-ok",
                RawJson = "{}",
                Rows =
                {
                    new CostByServiceQueryRecord
                    {
                        Label = "Azure Storage",
                        TotalCost = 12.5m,
                        Currency = "BRL",
                        Count = 1
                    }
                }
            });

        costClient.Setup(c => c.QueryCostByServiceAsync(
                "sub-fail",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                "None",
                null,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("forced error"));

        repository.Setup(r => r.SaveByServiceAsync(
                It.IsAny<DateTime>(),
                "sub-ok",
                It.IsAny<IReadOnlyCollection<CostByServiceRow>>(),
                "{}",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new CostByServiceManualFunction(
            costClient.Object,
            repository.Object,
            subscriptionDiscoveryService: null!,
            config,
            new NullLogger<CostByServiceManualFunction>());

        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/CostByServiceManualRun?date=2026-02-22&subscription=all");
        var response = await sut.RunAsync(req);
        var body = await HttpTestHelpers.ReadBodyAsStringAsync(response);
        using var doc = JsonDocument.Parse(body);

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        doc.RootElement.GetProperty("successCount").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("failureCount").GetInt32().Should().Be(1);
        repository.VerifyAll();
    }

    [Fact]
    public async Task RunAsync_WhenSubscriptionIsProvided_ShouldUseOnlyRequestedSubscription()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["COST_SUBSCRIPTIONS"] = "sub-a,sub-b"
        });

        var costClient = new Mock<ICostManagementClient>(MockBehavior.Strict);
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);

        costClient.Setup(c => c.QueryCostByServiceAsync(
                "sub-custom",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                "None",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostByServiceQueryResponse
            {
                SubscriptionId = "sub-custom",
                RawJson = "{}",
                Rows =
                {
                    new CostByServiceQueryRecord
                    {
                        Label = "Azure App Service",
                        TotalCost = 20m,
                        Currency = "BRL",
                        Count = 1
                    }
                }
            });

        repository.Setup(r => r.SaveByServiceAsync(
                It.IsAny<DateTime>(),
                "sub-custom",
                It.IsAny<IReadOnlyCollection<CostByServiceRow>>(),
                "{}",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new CostByServiceManualFunction(
            costClient.Object,
            repository.Object,
            subscriptionDiscoveryService: null!,
            config,
            new NullLogger<CostByServiceManualFunction>());

        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/CostByServiceManualRun?subscription=sub-custom");
        var response = await sut.RunAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        costClient.Verify(c => c.QueryCostByServiceAsync(
            "sub-custom",
            It.IsAny<DateTime>(),
            It.IsAny<DateTime>(),
            "None",
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
