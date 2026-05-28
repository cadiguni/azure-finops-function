using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class CostByServiceManualFunction
{
    private readonly ICostManagementClient _costManagementClient;
    private readonly ICostStorageRepository _costStorageRepository;
    private readonly SubscriptionDiscoveryService _subscriptionDiscoveryService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostByServiceManualFunction> _logger;

    public CostByServiceManualFunction(
        ICostManagementClient costManagementClient,
        ICostStorageRepository costStorageRepository,
        SubscriptionDiscoveryService subscriptionDiscoveryService,
        IConfiguration configuration,
        ILogger<CostByServiceManualFunction> logger)
    {
        _costManagementClient = costManagementClient;
        _costStorageRepository = costStorageRepository;
        _subscriptionDiscoveryService = subscriptionDiscoveryService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/CostByServiceManualRun?date=2026-02-22&subscription=all
    /// GET /api/CostByServiceManualRun?date=2026-02-22&subscription=<subscription-id>
    /// </summary>
    [Function("CostByServiceManualRun")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
    {
        try
        {
            var targetDate = ParseDate(req.Query?["date"]) ?? DateTime.UtcNow.Date.AddDays(-1);
            var subscriptionFilter = req.Query?["subscription"] ?? "all";
            var subscriptions = await ResolveSubscriptionsAsync(subscriptionFilter);

            var startedAt = DateTime.UtcNow;
            var success = new List<object>();
            var failures = new List<object>();

            _logger.LogInformation(
                "Execução manual de custo por serviço iniciada. date={date}, filter={filter}, subscriptions={count}",
                targetDate.ToString("yyyy-MM-dd"),
                subscriptionFilter,
                subscriptions.Count);

            foreach (var subscriptionId in subscriptions)
            {
                try
                {
                    var queryResult = await _costManagementClient.QueryCostByServiceAsync(
                        subscriptionId,
                        targetDate,
                        targetDate,
                        granularity: "None");

                    var byServiceRows = queryResult.Rows
                        .GroupBy(r => new { r.Label, r.Currency })
                        .Select(g => new CostByServiceRow
                        {
                            Label = g.Key.Label,
                            Currency = g.Key.Currency,
                            TotalCost = g.Sum(x => x.TotalCost),
                            Count = g.Sum(x => x.Count),
                            SubscriptionId = subscriptionId
                        })
                        .OrderByDescending(r => r.TotalCost)
                        .ToList();

                    await _costStorageRepository.SaveByServiceAsync(
                        targetDate,
                        subscriptionId,
                        byServiceRows,
                        queryResult.RawJson);

                    success.Add(new
                    {
                        subscriptionId,
                        rows = byServiceRows.Count,
                        totalCost = byServiceRows.Sum(x => x.TotalCost),
                        currency = byServiceRows.FirstOrDefault()?.Currency ?? "BRL"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha na execução manual para subscription {subscriptionId}", subscriptionId);
                    failures.Add(new
                    {
                        subscriptionId,
                        error = ex.Message
                    });
                }
            }

            var endedAt = DateTime.UtcNow;
            var response = req.CreateResponse(failures.Count == 0 ? HttpStatusCode.OK : HttpStatusCode.MultiStatus);
            await response.WriteAsJsonAsync(new
            {
                startedAt,
                endedAt,
                date = targetDate.ToString("yyyy-MM-dd"),
                requestedFilter = subscriptionFilter,
                totalSubscriptions = subscriptions.Count,
                successCount = success.Count,
                failureCount = failures.Count,
                success,
                failures
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro não tratado em CostByServiceManualRun.");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new
            {
                error = "Falha ao executar CostByServiceManualRun",
                message = ex.Message
            });
            return response;
        }
    }

    private DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return DateTime.TryParseExact(
            text,
            "yyyy-MM-dd",
            null,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var date)
            ? date.Date
            : null;
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
}
