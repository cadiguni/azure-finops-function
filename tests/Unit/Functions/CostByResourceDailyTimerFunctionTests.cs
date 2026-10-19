using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Functions;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Functions;

public class CostByResourceDailyTimerFunctionTests
{
    [Fact]
    public async Task RunAsync_ShouldSendSingleStarterMessage()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["EnableLegacyCostByResourceCollection"] = "true",
            ["COST_RESOURCE_SERVICE"] = "Azure App Service",
            ["COST_DATA_DELAY_DAYS"] = "3",
            ["ENABLE_QUEUE_PROCESSING"] = "true",
            ["QUEUE_COST_BY_RESOURCE_STARTER"] = "cost-by-resource-starter"
        });

        ServiceBusMessage? sentMessage = null;
        var senderMock = new Mock<ServiceBusSender>(MockBehavior.Strict);
        senderMock
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((message, _) => sentMessage = message)
            .Returns(Task.CompletedTask);
        senderMock
            .Setup(s => s.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        var clientMock = new Mock<ServiceBusClient>(MockBehavior.Strict);
        clientMock
            .Setup(c => c.CreateSender("cost-by-resource-starter"))
            .Returns(senderMock.Object);

        var queueService = new QueueService(clientMock.Object, configuration, new NullLogger<QueueService>());

        var sut = new CostByResourceDailyTimerFunction(
            queueService,
            configuration,
            new NullLogger<CostByResourceDailyTimerFunction>());

        await sut.RunAsync(null!);

        senderMock.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        sentMessage.Should().NotBeNull();
        using var payload = JsonDocument.Parse(sentMessage!.Body.ToString());
        payload.RootElement.GetProperty("Date").GetDateTime().Date
            .Should().Be(DateTime.UtcNow.Date.AddDays(-3));
    }

    [Fact]
    public async Task RunAsync_WhenStarterSendFails_ShouldNotThrow()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["EnableLegacyCostByResourceCollection"] = "true",
            ["COST_RESOURCE_SERVICE"] = "Azure App Service",
            ["ENABLE_QUEUE_PROCESSING"] = "true",
            ["QUEUE_COST_BY_RESOURCE_STARTER"] = "cost-by-resource-starter"
        });

        var senderMock = new Mock<ServiceBusSender>(MockBehavior.Strict);
        senderMock
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("queue unavailable"));
        senderMock
            .Setup(s => s.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        var clientMock = new Mock<ServiceBusClient>(MockBehavior.Strict);
        clientMock
            .Setup(c => c.CreateSender("cost-by-resource-starter"))
            .Returns(senderMock.Object);

        var queueService = new QueueService(clientMock.Object, configuration, new NullLogger<QueueService>());

        var sut = new CostByResourceDailyTimerFunction(
            queueService,
            configuration,
            new NullLogger<CostByResourceDailyTimerFunction>());

        await sut.RunAsync(null!);

        senderMock.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhenServiceFilterIsNotConfigured_ShouldRequestAllServices()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["EnableLegacyCostByResourceCollection"] = "true",
            ["ENABLE_QUEUE_PROCESSING"] = "true",
            ["QUEUE_COST_BY_RESOURCE_STARTER"] = "cost-by-resource-starter"
        });
        ServiceBusMessage? sentMessage = null;
        var senderMock = new Mock<ServiceBusSender>(MockBehavior.Strict);
        senderMock.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((message, _) => sentMessage = message)
            .Returns(Task.CompletedTask);
        senderMock.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var clientMock = new Mock<ServiceBusClient>(MockBehavior.Strict);
        clientMock.Setup(c => c.CreateSender("cost-by-resource-starter")).Returns(senderMock.Object);

        var sut = new CostByResourceDailyTimerFunction(
            new QueueService(clientMock.Object, configuration, new NullLogger<QueueService>()),
            configuration,
            new NullLogger<CostByResourceDailyTimerFunction>());

        await sut.RunAsync(null!);

        using var payload = JsonDocument.Parse(sentMessage!.Body.ToString());
        payload.RootElement.GetProperty("Service").GetString().Should().BeNull();
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
