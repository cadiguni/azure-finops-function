using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public class QueueService
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ILogger<QueueService> _logger;
    private readonly bool _queueProcessingEnabled;

    private readonly string _subscriptionAnalysisQueue;
    private readonly string _storageAnalysisQueue;
    private readonly string _vmAnalysisQueue;
    private readonly string _appServiceAnalysisQueue;
    private readonly string _resultsQueue;

    public QueueService(
        ServiceBusClient serviceBusClient,
        IConfiguration configuration,
        ILogger<QueueService> logger)
    {
        _serviceBusClient = serviceBusClient;
        _logger = logger;

        _queueProcessingEnabled = configuration.GetValue<bool>("ENABLE_QUEUE_PROCESSING", false);

        _subscriptionAnalysisQueue = configuration["QUEUE_SUBSCRIPTION_ANALYSIS"] ?? "subscription-analysis";
        _storageAnalysisQueue = configuration["QUEUE_STORAGE_ANALYSIS"] ?? "storage-analysis";
        _vmAnalysisQueue = configuration["QUEUE_VM_ANALYSIS"] ?? "vm-analysis";
        _appServiceAnalysisQueue = configuration["QUEUE_APPSERVICE_ANALYSIS"] ?? "appservice-analysis";
        _resultsQueue = configuration["QUEUE_ANALYSIS_RESULTS"] ?? "analysis-results";

        _logger.LogInformation("QueueService started - queue processing enabled: {enabled}", _queueProcessingEnabled);
    }

    public bool IsQueueProcessingEnabled => _queueProcessingEnabled;

    public async Task<bool> SendSubscriptionAnalysisAsync(string subscriptionId, string analysisType = "complete")
    {
        if (!_queueProcessingEnabled)
        {
            _logger.LogInformation("Queue processing disabled - skipping queue send");
            return false;
        }

        try
        {
            var message = new
            {
                SubscriptionId = subscriptionId,
                AnalysisType = analysisType,
                Timestamp = DateTime.UtcNow,
                RequestId = Guid.NewGuid().ToString()
            };

            var serviceBusMessage = new ServiceBusMessage(JsonSerializer.Serialize(message))
            {
                MessageId = Guid.NewGuid().ToString(),
                Subject = $"subscription-analysis-{analysisType}"
            };

            var sender = _serviceBusClient.CreateSender(_subscriptionAnalysisQueue);
            await sender.SendMessageAsync(serviceBusMessage);
            await sender.DisposeAsync();

            _logger.LogInformation(
                "Subscription {subscriptionId} sent to queue {queue} - analysis: {analysisType}",
                subscriptionId,
                _subscriptionAnalysisQueue,
                analysisType);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending subscription {subscriptionId} to queue", subscriptionId);
            return false;
        }
    }

    public async Task<bool> SendStorageAnalysisAsync(string subscriptionId, List<string> storageAccountIds)
    {
        if (!_queueProcessingEnabled)
        {
            return false;
        }

        try
        {
            var message = new
            {
                SubscriptionId = subscriptionId,
                StorageAccounts = storageAccountIds,
                AnalysisType = "storage",
                Timestamp = DateTime.UtcNow,
                RequestId = Guid.NewGuid().ToString()
            };

            var serviceBusMessage = new ServiceBusMessage(JsonSerializer.Serialize(message))
            {
                MessageId = Guid.NewGuid().ToString(),
                Subject = "storage-analysis"
            };

            var sender = _serviceBusClient.CreateSender(_storageAnalysisQueue);
            await sender.SendMessageAsync(serviceBusMessage);
            await sender.DisposeAsync();

            _logger.LogInformation("Storage analysis for {subscriptionId} sent - {count} storage accounts", subscriptionId, storageAccountIds.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending storage analysis to queue");
            return false;
        }
    }

    public async Task<bool> SendVmAnalysisAsync(string subscriptionId, List<string> vmIds)
    {
        if (!_queueProcessingEnabled)
        {
            return false;
        }

        try
        {
            var message = new
            {
                SubscriptionId = subscriptionId,
                VmIds = vmIds,
                AnalysisType = "vm",
                Timestamp = DateTime.UtcNow,
                RequestId = Guid.NewGuid().ToString()
            };

            var serviceBusMessage = new ServiceBusMessage(JsonSerializer.Serialize(message))
            {
                MessageId = Guid.NewGuid().ToString(),
                Subject = "vm-analysis"
            };

            var sender = _serviceBusClient.CreateSender(_vmAnalysisQueue);
            await sender.SendMessageAsync(serviceBusMessage);
            await sender.DisposeAsync();

            _logger.LogInformation("VM analysis for {subscriptionId} sent - {count} VMs", subscriptionId, vmIds.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending VM analysis to queue");
            return false;
        }
    }

    public async Task<bool> SendAppServiceAnalysisAsync(string subscriptionId, List<string> appServiceIds)
    {
        if (!_queueProcessingEnabled)
        {
            return false;
        }

        try
        {
            var message = new
            {
                SubscriptionId = subscriptionId,
                AppServices = appServiceIds,
                AnalysisType = "appservice",
                Timestamp = DateTime.UtcNow,
                RequestId = Guid.NewGuid().ToString()
            };

            var serviceBusMessage = new ServiceBusMessage(JsonSerializer.Serialize(message))
            {
                MessageId = Guid.NewGuid().ToString(),
                Subject = "appservice-analysis"
            };

            var sender = _serviceBusClient.CreateSender(_appServiceAnalysisQueue);
            await sender.SendMessageAsync(serviceBusMessage);
            await sender.DisposeAsync();

            _logger.LogInformation("App Service analysis for {subscriptionId} sent - {count} apps", subscriptionId, appServiceIds.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending App Service analysis to queue");
            return false;
        }
    }

    public async Task<bool> SendAnalysisResultsAsync(object analysisResults, string analysisType, string subscriptionId)
    {
        if (!_queueProcessingEnabled)
        {
            return false;
        }

        try
        {
            var message = new
            {
                SubscriptionId = subscriptionId,
                AnalysisType = analysisType,
                AnalysisResult = analysisResults,
                Timestamp = DateTime.UtcNow,
                RequestId = Guid.NewGuid().ToString()
            };

            var serviceBusMessage = new ServiceBusMessage(JsonSerializer.Serialize(message))
            {
                MessageId = Guid.NewGuid().ToString(),
                Subject = $"results-{analysisType}"
            };

            var sender = _serviceBusClient.CreateSender(_resultsQueue);
            await sender.SendMessageAsync(serviceBusMessage);
            await sender.DisposeAsync();

            _logger.LogInformation("Analysis results sent to queue - type: {analysisType}", analysisType);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending analysis results to queue");
            return false;
        }
    }

    public async Task<int> SendBulkSubscriptionAnalysisAsync(List<string> subscriptionIds, string analysisType = "complete")
    {
        if (!_queueProcessingEnabled)
        {
            _logger.LogInformation("Queue processing disabled - skipping bulk send");
            return 0;
        }

        var successCount = 0;

        var tasks = subscriptionIds.Select(async subscriptionId =>
        {
            var success = await SendSubscriptionAnalysisAsync(subscriptionId, analysisType);
            if (success)
            {
                Interlocked.Increment(ref successCount);
            }
        });

        await Task.WhenAll(tasks);
        return successCount;
    }

    public async Task SendScheduledMessageAsync(
        string queueName,
        string messageBody,
        DateTimeOffset scheduledEnqueueTime,
        Dictionary<string, object>? applicationProperties = null)
    {
        var sender = _serviceBusClient.CreateSender(queueName);
        var message = new ServiceBusMessage(messageBody)
        {
            ScheduledEnqueueTime = scheduledEnqueueTime
        };

        if (applicationProperties != null)
        {
            foreach (var prop in applicationProperties)
            {
                message.ApplicationProperties[prop.Key] = prop.Value;
            }
        }

        await sender.SendMessageAsync(message);
    }

    public async Task ScheduleMessageAsync(
        string queueName,
        string messageBody,
        DateTimeOffset scheduledEnqueueTime,
        IDictionary<string, object>? properties = null)
    {
        var sender = _serviceBusClient.CreateSender(queueName);
        var message = new ServiceBusMessage(messageBody)
        {
            ScheduledEnqueueTime = scheduledEnqueueTime,
            MessageId = Guid.NewGuid().ToString()
        };

        if (properties != null)
        {
            foreach (var prop in properties)
            {
                message.ApplicationProperties[prop.Key] = prop.Value;
            }
        }

        await sender.SendMessageAsync(message);
    }

    public async Task SendStepMessageAsync(object stepMessage)
    {
        var json = JsonSerializer.Serialize(stepMessage);
        var message = new ServiceBusMessage(json)
        {
            MessageId = $"step-{Guid.NewGuid()}",
            Subject = "Analysis Step Processing",
            TimeToLive = TimeSpan.FromDays(1)
        };

        var stepsSender = _serviceBusClient.CreateSender("subscription-analysis-steps");
        await stepsSender.SendMessageAsync(message);
        await stepsSender.DisposeAsync();
    }
}
