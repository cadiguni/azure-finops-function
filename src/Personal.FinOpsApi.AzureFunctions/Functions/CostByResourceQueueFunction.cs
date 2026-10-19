using Azure.Messaging.ServiceBus;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class CostByResourceQueueFunction
{
    private readonly ICostManagementClient _costManagementClient;
    private readonly ICostStorageRepository _costStorageRepository;
    private readonly QueueService _queueService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostByResourceQueueFunction> _logger;

    public CostByResourceQueueFunction(
        ICostManagementClient costManagementClient,
        ICostStorageRepository costStorageRepository,
        QueueService queueService,
        IConfiguration configuration,
        ILogger<CostByResourceQueueFunction> logger)
    {
        _costManagementClient = costManagementClient;
        _costStorageRepository = costStorageRepository;
        _queueService = queueService;
        _configuration = configuration;
        _logger = logger;
    }

    [Function("CostByResourceQueue")]
    public async Task RunAsync(
        [ServiceBusTrigger("%QUEUE_COST_BY_RESOURCE%", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message)
    {
        var payload = ParsePayload(message.Body.ToString());
        if (payload == null || string.IsNullOrWhiteSpace(payload.SubscriptionId))
        {
            _logger.LogError("Mensagem inválida em CostByResourceQueue: {body}", message.Body.ToString());
            return;
        }

        if (!CostCollectionRouting.IsLegacyByResourceEnabled(_configuration) ||
            CostCollectionRouting.IsCutoverSubscription(_configuration, payload.SubscriptionId))
        {
            _logger.LogInformation(
                "Mensagem legada por recurso ignorada: subscriptionId={subscriptionId} source={source} reason=export-routed-or-disabled",
                payload.SubscriptionId,
                CostDataSources.CostManagementQueryApi);
            return;
        }

        var targetDate = payload.Date;
        var serviceFilter = NormalizeServiceFilter(payload.Service);

        try
        {
            var queryResult = await _costManagementClient.QueryCostByResourceAsync(
                payload.SubscriptionId,
                targetDate,
                targetDate,
                granularity: "None",
                serviceFilter: serviceFilter);

            if (queryResult.Rows.Count == 0 && !string.IsNullOrWhiteSpace(serviceFilter))
            {
                queryResult = await _costManagementClient.QueryCostByResourceAsync(
                    payload.SubscriptionId,
                    targetDate,
                    targetDate,
                    granularity: "None",
                    serviceFilter: null);
            }

            if (queryResult.Rows.Count == 0)
            {
                throw new CostDataNotAvailableException(
                    $"Cost Management não retornou dados consolidados para {payload.SubscriptionId} em {targetDate:yyyy-MM-dd}.");
            }

            var byResourceRows = queryResult.Rows
                .Where(r => MatchesServiceFilter(r.ServiceName, serviceFilter))
                .GroupBy(r => new { r.ResourceId, r.Label, r.Currency, r.ServiceName })
                .Select(g => new CostByResourceRow
                {
                    ResourceId = g.Key.ResourceId,
                    Label = g.Key.Label,
                    ServiceName = g.Key.ServiceName,
                    Currency = g.Key.Currency,
                    TotalCost = g.Sum(x => x.TotalCost),
                    Count = g.Sum(x => x.Count),
                    SubscriptionId = payload.SubscriptionId
                })
                .OrderByDescending(r => r.TotalCost)
                .ToList();

            await _costStorageRepository.SaveByResourceAsync(
                targetDate,
                payload.SubscriptionId,
                byResourceRows,
                queryResult.RawJson);

            _logger.LogInformation(
                "CostByResourceQueue concluída | sub={subscriptionId} date={date} rows={rows}",
                payload.SubscriptionId,
                targetDate.ToString("yyyy-MM-dd"),
                byResourceRows.Count);
        }
        catch (Exception ex) when (IsRetryableCostFailure(ex) && payload.RetryCount < 4)
        {
            var nextRetry = payload.RetryCount + 1;
            var delayMinutes = ex is CostDataNotAvailableException
                ? nextRetry switch
                {
                    1 => 30,
                    2 => 60,
                    3 => 120,
                    _ => 240
                }
                : nextRetry switch
                {
                    1 => 2,
                    2 => 5,
                    3 => 10,
                    _ => 20
                };

            var retryPayload = new CostByResourceQueueMessage
            {
                SubscriptionId = payload.SubscriptionId,
                Date = targetDate,
                Service = serviceFilter,
                RetryCount = nextRetry
            };

            await _queueService.ScheduleMessageAsync(
                queueName: _queueService.CostByResourceQueueName,
                messageBody: JsonSerializer.Serialize(retryPayload),
                scheduledEnqueueTime: DateTimeOffset.UtcNow.AddMinutes(delayMinutes),
                properties: new Dictionary<string, object>
                {
                    ["retryCount"] = nextRetry,
                    ["isRetry"] = true
                });

            _logger.LogWarning(
                "CostByResourceQueue reagendada | sub={subscriptionId} reason={reason} retry={retry} delay={delay}min",
                payload.SubscriptionId,
                ex is CostDataNotAvailableException ? "no-data" : "429",
                nextRetry,
                delayMinutes);
        }
    }

    private static CostByResourceQueueMessage? ParsePayload(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            var payload = JsonSerializer.Deserialize<CostByResourceQueueMessage>(body);
            if (payload == null || string.IsNullOrWhiteSpace(payload.SubscriptionId))
                return null;

            if (payload.Date == default)
            {
                payload.Date = DateTime.UtcNow.Date.AddDays(-1);
            }

            return payload;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTooManyRequests(HttpRequestException ex)
    {
        return ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRetryableCostFailure(Exception ex)
    {
        return ex is CostDataNotAvailableException ||
               ex is HttpRequestException httpException && IsTooManyRequests(httpException);
    }

    private static bool MatchesServiceFilter(string candidate, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return true;

        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var a = candidate.Trim();
        var b = requested.Trim();
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
               a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
               b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeServiceFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();
    }

    private class CostByResourceQueueMessage
    {
        public string SubscriptionId { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public string? Service { get; set; }
        public int RetryCount { get; set; }
    }

    private sealed class CostDataNotAvailableException(string message) : Exception(message);
}
