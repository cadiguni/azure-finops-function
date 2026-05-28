using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class CostByResourceManualFunction
{
    private readonly ICostManagementClient _costManagementClient;
    private readonly ICostStorageRepository _costStorageRepository;
    private readonly SubscriptionDiscoveryService _subscriptionDiscoveryService;
    private readonly QueueService _queueService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostByResourceManualFunction> _logger;

    public CostByResourceManualFunction(
        ICostManagementClient costManagementClient,
        ICostStorageRepository costStorageRepository,
        SubscriptionDiscoveryService subscriptionDiscoveryService,
        QueueService queueService,
        IConfiguration configuration,
        ILogger<CostByResourceManualFunction> logger)
    {
        _costManagementClient = costManagementClient;
        _costStorageRepository = costStorageRepository;
        _subscriptionDiscoveryService = subscriptionDiscoveryService;
        _queueService = queueService;
        _configuration = configuration;
        _logger = logger;
    }

    [Function("CostByResourceManualRun")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
    {
        try
        {
            var targetDate = ParseDate(req.Query?["date"]) ?? DateTime.UtcNow.Date.AddDays(-1);
            var subscriptionFilter = req.Query?["subscription"] ?? "all";
            var serviceNameRaw = req.Query?["service"] ?? _configuration["COST_RESOURCE_SERVICE"] ?? "Azure App Service";
            var serviceName = NormalizeServiceFilter(serviceNameRaw);
            var subscriptions = await ResolveSubscriptionsAsync(subscriptionFilter);

            var startedAt = DateTime.UtcNow;
            var success = new List<object>();
            var failures = new List<object>();

            foreach (var subscriptionId in subscriptions)
            {
                try
                {
                    var queryResult = await _costManagementClient.QueryCostByResourceAsync(
                        subscriptionId,
                        targetDate,
                        targetDate,
                        granularity: "None",
                        serviceFilter: serviceName);

                    if (queryResult.Rows.Count == 0 && !string.IsNullOrWhiteSpace(serviceName))
                    {
                        _logger.LogWarning(
                            "Nenhuma linha para service filter '{service}'. Tentando consulta sem filtro para {subscriptionId}.",
                            serviceName,
                            subscriptionId);

                        queryResult = await _costManagementClient.QueryCostByResourceAsync(
                            subscriptionId,
                            targetDate,
                            targetDate,
                            granularity: "None",
                            serviceFilter: null);
                    }

                    var byResourceRows = queryResult.Rows
                        .Where(r => MatchesServiceFilter(r.ServiceName, serviceName))
                        .GroupBy(r => new { r.ResourceId, r.Label, r.Currency, r.ServiceName })
                        .Select(g => new CostByResourceRow
                        {
                            ResourceId = g.Key.ResourceId,
                            Label = g.Key.Label,
                            ServiceName = g.Key.ServiceName,
                            Currency = g.Key.Currency,
                            TotalCost = g.Sum(x => x.TotalCost),
                            Count = g.Sum(x => x.Count),
                            SubscriptionId = subscriptionId
                        })
                        .OrderByDescending(r => r.TotalCost)
                        .ToList();

                    await _costStorageRepository.SaveByResourceAsync(
                        targetDate,
                        subscriptionId,
                        byResourceRows,
                        queryResult.RawJson);

                    success.Add(new
                    {
                        subscriptionId,
                        rows = byResourceRows.Count,
                        totalCost = byResourceRows.Sum(x => x.TotalCost),
                        currency = byResourceRows.FirstOrDefault()?.Currency ?? "BRL"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha na execução manual by-resource para subscription {subscriptionId}", subscriptionId);
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
                service = serviceName ?? "all",
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
            _logger.LogError(ex, "Erro não tratado em CostByResourceManualRun.");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new
            {
                error = "Falha ao executar CostByResourceManualRun"
            });
            return response;
        }
    }

    // REMOVED: CostByResourceQueueRun - Funcionalidade duplicada
    // A função CostByResourceManualRun já suporta queue processing quando habilitado
    // Use apenas: /api/CostByResourceManualRun (com lógica híbrida queue/direct)
    /*
    [Function("CostByResourceQueueRun")]
    public async Task<HttpResponseData> QueueRunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
    {
        // REMOVED: Esta funcionalidade foi incorporada à CostByResourceManualRun
        // A função principal agora decide automaticamente entre queue e execução direta
        var response = req.CreateResponse(HttpStatusCode.Gone);
        await response.WriteAsJsonAsync(new
        {
            error = "API removida - use /api/CostByResourceManualRun que suporta queue automaticamente",
            redirect = "/api/CostByResourceManualRun"
        });
        return response;
    }
    */

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
}
