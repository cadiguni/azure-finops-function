using Azure.Messaging.ServiceBus;
using Personal.FinOpsApi.AzureFunctions.Functions;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Functions;

public class CostByResourceDailyTimerFunctionTests
{
    [Fact]
    public async Task RunAsync_ShouldSendSingleStarterMessage()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["COST_RESOURCE_SERVICE"] = "Azure App Service",
            ["ENABLE_QUEUE_PROCESSING"] = "true",
            ["QUEUE_COST_BY_RESOURCE_STARTER"] = "cost-by-resource-starter"
        });

        var senderMock = new Mock<ServiceBusSender>(MockBehavior.Strict);
        senderMock
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
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
    }

    [Fact]
    public async Task RunAsync_WhenStarterSendFails_ShouldNotThrow()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
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

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
