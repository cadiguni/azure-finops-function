using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Functions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Functions;

public class CostByServiceDailyTimerFunctionTests
{
    [Fact]
    public async Task RunAsync_WhenCostSubscriptionsConfigured_ShouldProcessAllSubscriptions()
    {
        var subscriptions = new[] { "sub-1", "sub-2" };
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["COST_SUBSCRIPTIONS"] = string.Join(",", subscriptions)
        });

        var costClientMock = new Mock<ICostManagementClient>(MockBehavior.Strict);
        var repositoryMock = new Mock<ICostStorageRepository>(MockBehavior.Strict);

        foreach (var sub in subscriptions)
        {
            costClientMock
                .Setup(c => c.QueryCostByServiceAsync(
                    sub,
                    It.IsAny<DateTime>(),
                    It.IsAny<DateTime>(),
                    "None",
                    null,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CostByServiceQueryResponse
                {
                    SubscriptionId = sub,
                    Currency = "BRL",
                    Rows = new List<CostByServiceQueryRecord>
                    {
                        new()
                        {
                            SubscriptionId = sub,
                            Label = "Azure Storage",
                            TotalCost = 10m,
                            Currency = "BRL"
                        }
                    },
                    RawJson = "{}"
                });

            repositoryMock
                .Setup(r => r.SaveByServiceAsync(
                    It.IsAny<DateTime>(),
                    sub,
                    It.Is<IReadOnlyCollection<CostByServiceRow>>(rows => rows.Count == 1),
                    "{}",
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        var sut = new CostByServiceDailyTimerFunction(
            costClientMock.Object,
            repositoryMock.Object,
            subscriptionDiscoveryService: null!,
            configuration,
            new NullLogger<CostByServiceDailyTimerFunction>());

        await sut.RunAsync(null!);

        foreach (var sub in subscriptions)
        {
            costClientMock.Verify(c => c.QueryCostByServiceAsync(
                sub,
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                "None",
                null,
                It.IsAny<CancellationToken>()), Times.Once);

            repositoryMock.Verify(r => r.SaveByServiceAsync(
                It.IsAny<DateTime>(),
                sub,
                It.IsAny<IReadOnlyCollection<CostByServiceRow>>(),
                "{}",
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task RunAsync_WhenOneSubscriptionFails_ShouldContinueWithOthers()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["COST_SUBSCRIPTIONS"] = "sub-fail,sub-ok"
        });

        var costClientMock = new Mock<ICostManagementClient>(MockBehavior.Strict);
        var repositoryMock = new Mock<ICostStorageRepository>(MockBehavior.Strict);

        costClientMock
            .Setup(c => c.QueryCostByServiceAsync(
                "sub-fail",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                "None",
                null,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("429"));

        costClientMock
            .Setup(c => c.QueryCostByServiceAsync(
                "sub-ok",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                "None",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostByServiceQueryResponse
            {
                SubscriptionId = "sub-ok",
                Rows = new List<CostByServiceQueryRecord>
                {
                    new()
                    {
                        SubscriptionId = "sub-ok",
                        Label = "Azure App Service",
                        TotalCost = 15m,
                        Currency = "BRL"
                    }
                },
                RawJson = "{}"
            });

        repositoryMock
            .Setup(r => r.SaveByServiceAsync(
                It.IsAny<DateTime>(),
                "sub-ok",
                It.IsAny<IReadOnlyCollection<CostByServiceRow>>(),
                "{}",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new CostByServiceDailyTimerFunction(
            costClientMock.Object,
            repositoryMock.Object,
            subscriptionDiscoveryService: null!,
            configuration,
            new NullLogger<CostByServiceDailyTimerFunction>());

        await sut.RunAsync(null!);

        costClientMock.VerifyAll();
        repositoryMock.Verify(r => r.SaveByServiceAsync(
            It.IsAny<DateTime>(),
            "sub-ok",
            It.IsAny<IReadOnlyCollection<CostByServiceRow>>(),
            "{}",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
