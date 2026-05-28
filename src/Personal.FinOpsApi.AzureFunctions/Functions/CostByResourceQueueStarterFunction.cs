using Azure.Messaging.ServiceBus;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class CostByResourceQueueStarterFunction
{
    private readonly QueueService _queueService;
    private readonly SubscriptionDiscoveryService _subscriptionDiscoveryService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostByResourceQueueStarterFunction> _logger;

    public CostByResourceQueueStarterFunction(
        QueueService queueService,
        SubscriptionDiscoveryService subscriptionDiscoveryService,
        IConfiguration configuration,
        ILogger<CostByResourceQueueStarterFunction> logger)
    {
        _queueService = queueService;
        _subscriptionDiscoveryService = subscriptionDiscoveryService;
        _configuration = configuration;
        _logger = logger;
    }

    [Function("CostByResourceQueueStarter")]
    public async Task RunAsync(
        [ServiceBusTrigger("%QUEUE_COST_BY_RESOURCE_STARTER%", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message)
    {
        var payload = ParsePayload(message.Body.ToString());
        if (payload == null)
        {
            _logger.LogError("Mensagem inválida em CostByResourceQueueStarter: {body}", message.Body.ToString());
            return;
        }

        var targetDate = payload.Date;
        var service = NormalizeServiceFilter(payload.Service);
        var subscriptionFilter = payload.SubscriptionFilter ?? "all";
        var subscriptions = await ResolveSubscriptionsAsync(subscriptionFilter);

        var (enqueued, failed) = await _queueService.SendBulkCostByResourceAnalysisAsync(subscriptions, targetDate, service);

        _logger.LogInformation(
            "CostByResourceQueueStarter concluída | date={date} subs={subs} enqueued={enqueued} failed={failed}",
            targetDate.ToString("yyyy-MM-dd"),
            subscriptions.Count,
            enqueued,
            failed);
    }

    private async Task<List<string>> ResolveSubscriptionsAsync(string subscriptionFilter)
    {
        if (!string.Equals(subscriptionFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { subscriptionFilter.Trim() };
        }

        var raw = _configuration["COST_SUBSCRIPTIONS"];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var discovered = await _subscriptionDiscoveryService.DiscoverSubscriptionsAsync();
        if (discovered.Count > 0)
        {
            return discovered
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var single = _configuration["AZURE_SUBSCRIPTION_ID"];
        if (!string.IsNullOrWhiteSpace(single))
        {
            return new List<string> { single.Trim() };
        }

        return new List<string>();
    }

    private static CostByResourceStarterPayload? ParsePayload(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            var payload = JsonSerializer.Deserialize<CostByResourceStarterPayload>(body);
            if (payload == null)
                return null;

            if (payload.Date == default)
            {
                payload.Date = DateTime.UtcNow.Date.AddDays(-1);
            }

            payload.SubscriptionFilter ??= "all";
            return payload;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeServiceFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();
    }

    private class CostByResourceStarterPayload
    {
        public DateTime Date { get; set; }
        public string? Service { get; set; }
        public string? SubscriptionFilter { get; set; }
    }
}
