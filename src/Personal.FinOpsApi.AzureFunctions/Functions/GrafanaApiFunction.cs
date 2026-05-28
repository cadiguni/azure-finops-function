using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
///  API otimizada para Grafana - dados já agregados e tabulares
/// </summary>
public class GrafanaApiFunction
{
    private readonly GrafanaDataService _grafanaService;
    private readonly ICostStorageRepository _costStorageRepository;
    private readonly ILogger<GrafanaApiFunction> _logger;

    public GrafanaApiFunction(
        GrafanaDataService grafanaService,
        ICostStorageRepository costStorageRepository,
        ILogger<GrafanaApiFunction> logger)
    {
        _grafanaService = grafanaService;
        _costStorageRepository = costStorageRepository;
        _logger = logger;
    }

    // REMOVED: Redundant Grafana APIs - simplificação da arquitetura
    // 
    // APIS REMOVIDAS (funcionalidade duplicada):
    // - GrafanaSavingsByType → Use GrafanaCostByService + filtros  
    // - GrafanaSavingsBySubscription → Use GrafanaCostByService + group by
    // - GrafanaResourceDetails → Use GrafanaCostByResource + filtros
    //
    // Esta remoção reduz de 8 para 5 endpoints Grafana

    /// <summary>
    ///  Endpoint de teste para verificar se a API está funcionando
    /// GET /api/grafana/health
    /// </summary>
    [Function("GrafanaHealthCheck")]
    public async Task<HttpResponseData> HealthCheckAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "grafana/health")] HttpRequestData req)
    {
        var yesterday = DateTime.UtcNow.Date.AddDays(-1);
        var storageAccessOk = await _costStorageRepository.CanAccessStorageAsync();
        var yesterdayDataOk = await _costStorageRepository.ExistsByServiceDataAsync(yesterday);
        var status = storageAccessOk && yesterdayDataOk ? "ok" : "error";
        var httpStatus = storageAccessOk && yesterdayDataOk
            ? System.Net.HttpStatusCode.OK
            : System.Net.HttpStatusCode.ServiceUnavailable;

        var response = req.CreateResponse(httpStatus);
        await response.WriteAsJsonAsync(new
        {
            status,
            timestamp = DateTime.UtcNow,
            checks = new
            {
                storageAccess = storageAccessOk,
                yesterdayByServiceData = yesterdayDataOk,
                yesterday = yesterday.ToString("yyyy-MM-dd")
            }
        });
        return response;
    }

    /// <summary>
    /// GET /api/GrafanaCostByService?date=YYYY-MM-DD&subscription=all
    /// </summary>
    [Function("GrafanaCostByService")]
    public async Task<HttpResponseData> GetCostByServiceAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "GrafanaCostByService")] HttpRequestData req)
    {
        try
        {
            var dateText = req.Query?["date"];
            var date = ParseDateOrDefault(dateText, DateTime.UtcNow.Date.AddDays(-1));
            var subscription = req.Query?["subscription"] ?? "all";

            List<CostByServiceRow> rows;
            if (string.Equals(subscription, "all", StringComparison.OrdinalIgnoreCase))
            {
                rows = await _costStorageRepository.LoadByServiceAllAsync(date);
            }
            else
            {
                rows = await _costStorageRepository.LoadByServiceAsync(date, subscription);
            }

            var aggregated = rows
                .GroupBy(r => new { Label = r.Label, Currency = r.Currency })
                .Select(g => new CostByServiceRow
                {
                    Label = g.Key.Label,
                    TotalCost = g.Sum(x => x.TotalCost),
                    Currency = g.Key.Currency,
                    Count = g.Sum(x => Math.Max(1, x.Count))
                })
                .OrderByDescending(r => r.TotalCost)
                .ToList();

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(aggregated);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro no endpoint GrafanaCostByService");
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Erro interno ao consultar custos por serviço.");
            return errorResponse;
        }
    }

    /// <summary>
    /// GET /api/GrafanaCostTrendByService?from=YYYY-MM-DD&to=YYYY-MM-DD&subscription=all&service=Azure%20App%20Service
    /// </summary>
    [Function("GrafanaCostTrendByService")]
    public async Task<HttpResponseData> GetCostTrendByServiceAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "GrafanaCostTrendByService")] HttpRequestData req)
    {
        try
        {
            var from = ParseDateOrDefault(req.Query?["from"], DateTime.UtcNow.Date.AddDays(-7));
            var to = ParseDateOrDefault(req.Query?["to"], DateTime.UtcNow.Date.AddDays(-1));
            if (to < from)
            {
                (from, to) = (to, from);
            }

            var subscription = req.Query?["subscription"] ?? "all";
            var service = req.Query?["service"] ?? string.Empty;
            var trend = new List<CostByServiceTrendRow>();

            for (var date = from.Date; date <= to.Date; date = date.AddDays(1))
            {
                List<CostByServiceRow> dayRows;
                if (string.Equals(subscription, "all", StringComparison.OrdinalIgnoreCase))
                {
                    dayRows = await _costStorageRepository.LoadByServiceAllAsync(date);
                }
                else
                {
                    dayRows = await _costStorageRepository.LoadByServiceAsync(date, subscription);
                }

                var filtered = string.IsNullOrWhiteSpace(service)
                    ? dayRows
                    : dayRows.Where(r => string.Equals(r.Label, service, StringComparison.OrdinalIgnoreCase)).ToList();

                var currencies = filtered
                    .Select(x => x.Currency)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                trend.Add(new CostByServiceTrendRow
                {
                    Date = date.ToString("yyyy-MM-dd"),
                    TotalCost = filtered.Sum(x => x.TotalCost),
                    Currency = currencies.Count switch
                    {
                        0 => "BRL",
                        1 => currencies[0],
                        _ => "MIXED"
                    }
                });
            }

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(trend);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro no endpoint GrafanaCostTrendByService");
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Erro interno ao consultar tendência de custos por serviço.");
            return errorResponse;
        }
    }

    /// <summary>
    /// GET /api/GrafanaCostByResource?date=YYYY-MM-DD&subscription=all&service=Azure%20App%20Service
    /// </summary>
    [Function("GrafanaCostByResource")]
    public async Task<HttpResponseData> GetCostByResourceAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "GrafanaCostByResource")] HttpRequestData req)
    {
        try
        {
            var dateText = req.Query?["date"];
            var date = ParseDateOrDefault(dateText, DateTime.UtcNow.Date.AddDays(-1));
            var subscription = req.Query?["subscription"] ?? "all";
            var service = NormalizeServiceFilter(DecodeQueryValue(req.Query?["service"]) ?? "Azure App Service");

            List<CostByResourceRow> rows;
            if (string.Equals(subscription, "all", StringComparison.OrdinalIgnoreCase))
            {
                rows = await _costStorageRepository.LoadByResourceAllAsync(date);
            }
            else
            {
                rows = await _costStorageRepository.LoadByResourceAsync(date, subscription);
            }

            var filteredRows = string.IsNullOrWhiteSpace(service)
                ? rows
                : rows.Where(r => MatchesServiceFilter(r.ServiceName, service)).ToList();

            var aggregated = filteredRows
                .GroupBy(r => new { r.ResourceId, r.Label, r.Currency, r.ServiceName })
                .Select(g => new CostByResourceRow
                {
                    ResourceId = g.Key.ResourceId,
                    Label = g.Key.Label,
                    ServiceName = g.Key.ServiceName,
                    TotalCost = g.Sum(x => x.TotalCost),
                    Currency = g.Key.Currency,
                    Count = g.Sum(x => Math.Max(1, x.Count)),
                    SubscriptionId = ResolveSingleSubscriptionId(g.Select(x => x.SubscriptionId))
                })
                .OrderByDescending(r => r.TotalCost)
                .ToList();

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(aggregated);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro no endpoint GrafanaCostByResource");
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Erro interno ao consultar custos por recurso.");
            return errorResponse;
        }
    }

    /// <summary>
    /// GET /api/GrafanaCostTrendByResource?from=YYYY-MM-DD&to=YYYY-MM-DD&subscription=all&resource=<resourceNameOrId>&service=Azure%20App%20Service
    /// </summary>
    [Function("GrafanaCostTrendByResource")]
    public async Task<HttpResponseData> GetCostTrendByResourceAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "GrafanaCostTrendByResource")] HttpRequestData req)
    {
        try
        {
            var from = ParseDateOrDefault(req.Query?["from"], DateTime.UtcNow.Date.AddDays(-7));
            var to = ParseDateOrDefault(req.Query?["to"], DateTime.UtcNow.Date.AddDays(-1));
            if (to < from)
            {
                (from, to) = (to, from);
            }

            var subscription = req.Query?["subscription"] ?? "all";
            var resourceFilter = DecodeQueryValue(req.Query?["resource"]) ?? string.Empty;
            var serviceFilter = NormalizeServiceFilter(DecodeQueryValue(req.Query?["service"]) ?? "Azure App Service");
            var trend = new List<CostByResourceTrendRow>();

            for (var date = from.Date; date <= to.Date; date = date.AddDays(1))
            {
                List<CostByResourceRow> dayRows;
                if (string.Equals(subscription, "all", StringComparison.OrdinalIgnoreCase))
                {
                    dayRows = await _costStorageRepository.LoadByResourceAllAsync(date);
                }
                else
                {
                    dayRows = await _costStorageRepository.LoadByResourceAsync(date, subscription);
                }

                if (!string.IsNullOrWhiteSpace(serviceFilter))
                {
                    dayRows = dayRows
                        .Where(r => MatchesServiceFilter(r.ServiceName, serviceFilter))
                        .ToList();
                }

                var filtered = string.IsNullOrWhiteSpace(resourceFilter)
                    ? dayRows
                    : dayRows.Where(r =>
                        string.Equals(r.Label, resourceFilter, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(r.ResourceId, resourceFilter, StringComparison.OrdinalIgnoreCase)).ToList();

                var currencies = filtered
                    .Select(x => x.Currency)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                trend.Add(new CostByResourceTrendRow
                {
                    Date = date.ToString("yyyy-MM-dd"),
                    TotalCost = filtered.Sum(x => x.TotalCost),
                    Currency = currencies.Count switch
                    {
                        0 => "BRL",
                        1 => currencies[0],
                        _ => "MIXED"
                    }
                });
            }

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(trend);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro no endpoint GrafanaCostTrendByResource");
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Erro interno ao consultar tendência de custos por recurso.");
            return errorResponse;
        }
    }

    private static DateTime ParseDateOrDefault(string? dateText, DateTime defaultValue)
    {
        if (!string.IsNullOrWhiteSpace(dateText) &&
            DateTime.TryParseExact(dateText, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.Date;
        }

        return defaultValue.Date;
    }

    private static string? DecodeQueryValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return Uri.UnescapeDataString(value);
    }

    private static string? NormalizeServiceFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();
    }

    private static string? ResolveSingleSubscriptionId(IEnumerable<string?> subscriptions)
    {
        var distinct = subscriptions
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return distinct.Count == 1 ? distinct[0] : null;
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
}
