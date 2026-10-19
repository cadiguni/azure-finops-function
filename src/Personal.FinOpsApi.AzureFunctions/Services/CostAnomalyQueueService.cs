using Azure.Messaging.ServiceBus;
using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public interface ICostAnomalyQueueService
{
    string RunQueueName { get; }

    Task<string> EnqueueRunAsync(
        string triggeredBy,
        CancellationToken cancellationToken = default);

    Task EnqueueSubscriptionAsync(
        string runId,
        int subscriptionIndex,
        CancellationToken cancellationToken = default);
}

public sealed class CostAnomalyQueueService : ICostAnomalyQueueService
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly IConfiguration _configuration;

    public CostAnomalyQueueService(
        ServiceBusClient serviceBusClient,
        IConfiguration configuration)
    {
        _serviceBusClient = serviceBusClient;
        _configuration = configuration;
    }

    public string RunQueueName =>
        _configuration["QUEUE_COST_ANOMALY_RUN"] ?? "cost-anomalies-run";

    public async Task<string> EnqueueRunAsync(
        string triggeredBy,
        CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid().ToString();
        var payload = new CostAnomalyRunRequest
        {
            RequestedAt = DateTime.UtcNow,
            TriggeredBy = triggeredBy,
            RequestId = runId
        };
        var sender = _serviceBusClient.CreateSender(RunQueueName);
        try
        {
            await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(payload))
            {
                MessageId = runId,
                Subject = "cost-anomaly-run"
            }, cancellationToken);
        }
        finally
        {
            await sender.DisposeAsync();
        }

        return runId;
    }

    public async Task EnqueueSubscriptionAsync(
        string runId,
        int subscriptionIndex,
        CancellationToken cancellationToken = default)
    {
        var payload = new CostAnomalySubscriptionWorkItem
        {
            RunId = runId,
            SubscriptionIndex = subscriptionIndex
        };
        var sender = _serviceBusClient.CreateSender(
            _configuration["QUEUE_COST_ANOMALY_SUBSCRIPTION"] ?? "cost-anomalies-subscription");
        try
        {
            await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(payload))
            {
                MessageId = $"{runId}:{subscriptionIndex}",
                Subject = "cost-anomaly-subscription"
            }, cancellationToken);
        }
        finally
        {
            await sender.DisposeAsync();
        }
    }
}
