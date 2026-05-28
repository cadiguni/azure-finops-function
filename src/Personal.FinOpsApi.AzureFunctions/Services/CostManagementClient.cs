using Azure.Core;
using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public class CostManagementClient : ICostManagementClient
{
    private const string CostManagementScope = "https://management.azure.com/.default";
    private readonly TokenCredential _credential;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CostManagementClient> _logger;

    public CostManagementClient(
        TokenCredential credential,
        IHttpClientFactory httpClientFactory,
        ILogger<CostManagementClient> logger)
    {
        _credential = credential;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<CostByServiceQueryResponse> QueryCostByServiceAsync(
        string subscriptionId,
        DateTime dateStartUtc,
        DateTime dateEndUtc,
        string granularity,
        string? serviceFilter = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedGranularity = NormalizeGranularity(granularity);
        var responseContent = await ExecuteQueryAsync(
            subscriptionId,
            BuildQueryBody(dateStartUtc, dateEndUtc, normalizedGranularity, serviceFilter, new[] { "ServiceName" }),
            cancellationToken);

        return ParseServiceResponse(subscriptionId, responseContent);
    }

    public async Task<CostByResourceQueryResponse> QueryCostByResourceAsync(
        string subscriptionId,
        DateTime dateStartUtc,
        DateTime dateEndUtc,
        string granularity,
        string? serviceFilter = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedGranularity = NormalizeGranularity(granularity);
        var responseContent = await ExecuteQueryAsync(
            subscriptionId,
            BuildQueryBody(dateStartUtc, dateEndUtc, normalizedGranularity, serviceFilter, new[] { "ResourceId", "ServiceName" }),
            cancellationToken);

        return ParseResourceResponse(subscriptionId, responseContent, serviceFilter);
    }

    private async Task<string> ExecuteQueryAsync(
        string subscriptionId,
        object requestBody,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
            throw new ArgumentException("SubscriptionId é obrigatório", nameof(subscriptionId));

        var endpoint =
            $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/query?api-version=2023-03-01";

        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(new[] { CostManagementScope }),
            cancellationToken);

        var requestJson = JsonSerializer.Serialize(requestBody);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var httpClient = _httpClientFactory.CreateClient();
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return responseContent;
            }

            if (!IsTransient(response.StatusCode) || attempt == 5)
            {
                _logger.LogError(
                    "Cost query falhou para {subscriptionId} (status {status}): {content}",
                    subscriptionId,
                    (int)response.StatusCode,
                    responseContent);
                throw new HttpRequestException(
                    $"Cost query failed ({(int)response.StatusCode}) for {subscriptionId}: {responseContent}");
            }

            var delay = GetRetryDelay(response, attempt);
            _logger.LogWarning(
                "Cost query transitório para {subscriptionId} (status {status}) tentativa {attempt}/5. Retry em {delay}ms",
                subscriptionId,
                (int)response.StatusCode,
                attempt,
                delay.TotalMilliseconds);
            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Fluxo de retry inesperado na consulta de custo.");
    }

    private static object BuildQueryBody(
        DateTime dateStartUtc,
        DateTime dateEndUtc,
        string granularity,
        string? serviceFilter,
        IReadOnlyCollection<string> groupingDimensions)
    {
        var from = dateStartUtc.Date.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var to = dateEndUtc.Date.AddDays(1).AddTicks(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");

        object? filter = null;
        if (!string.IsNullOrWhiteSpace(serviceFilter))
        {
            filter = new
            {
                dimensions = new
                {
                    name = "ServiceName",
                    @operator = "In",
                    values = new[] { serviceFilter }
                }
            };
        }

        var grouping = groupingDimensions
            .Select(name => new
            {
                type = "Dimension",
                name
            })
            .ToArray();

        return new
        {
            type = "Usage",
            timeframe = "Custom",
            timePeriod = new
            {
                from,
                to
            },
            dataset = new
            {
                granularity,
                aggregation = new
                {
                    totalCost = new
                    {
                        name = "PreTaxCost",
                        function = "Sum"
                    }
                },
                grouping,
                filter
            }
        };
    }

    private CostByServiceQueryResponse ParseServiceResponse(string subscriptionId, string responseContent)
    {
        var result = new CostByServiceQueryResponse
        {
            SubscriptionId = subscriptionId,
            RawJson = responseContent
        };

        if (!TryGetQueryRows(responseContent, out var columns, out var rows))
            return result;

        int costIndex = FindColumn(columns, "PreTaxCost", "Cost");
        int serviceIndex = FindColumn(columns, "ServiceName", "Service", "ServiceFamily");
        int currencyIndex = FindColumn(columns, "Currency", "BillingCurrency", "CurrencyCode");
        int dateIndex = FindColumn(columns, "UsageDate", "Date");

        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Array)
                continue;

            var serviceName = ReadString(row, serviceIndex) ?? "Unknown";
            var currency = ReadString(row, currencyIndex) ?? "BRL";
            var totalCost = ReadDecimal(row, costIndex);
            var usageDate = ReadDate(row, dateIndex);

            result.Rows.Add(new CostByServiceQueryRecord
            {
                SubscriptionId = subscriptionId,
                Label = serviceName,
                Currency = currency,
                TotalCost = totalCost,
                UsageDate = usageDate,
                Count = 1
            });
        }

        if (result.Rows.Count > 0)
        {
            result.Currency = result.Rows.First().Currency;
        }

        return result;
    }

    private CostByResourceQueryResponse ParseResourceResponse(string subscriptionId, string responseContent, string? serviceFilter)
    {
        var result = new CostByResourceQueryResponse
        {
            SubscriptionId = subscriptionId,
            RawJson = responseContent
        };

        if (!TryGetQueryRows(responseContent, out var columns, out var rows))
            return result;

        int costIndex = FindColumn(columns, "PreTaxCost", "Cost");
        int resourceIdIndex = FindColumn(columns, "ResourceId", "InstanceId");
        int serviceIndex = FindColumn(columns, "ServiceName", "Service", "ServiceFamily");
        int currencyIndex = FindColumn(columns, "Currency", "BillingCurrency", "CurrencyCode");
        int dateIndex = FindColumn(columns, "UsageDate", "Date");

        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Array)
                continue;

            var resourceId = ReadString(row, resourceIdIndex) ?? "unknown";
            var currency = ReadString(row, currencyIndex) ?? "BRL";
            var totalCost = ReadDecimal(row, costIndex);
            var usageDate = ReadDate(row, dateIndex);
            var serviceName = ReadString(row, serviceIndex) ?? serviceFilter ?? "Unknown";

            result.Rows.Add(new CostByResourceQueryRecord
            {
                SubscriptionId = subscriptionId,
                ResourceId = resourceId,
                Label = ExtractResourceName(resourceId),
                ServiceName = serviceName,
                Currency = currency,
                TotalCost = totalCost,
                UsageDate = usageDate,
                Count = 1
            });
        }

        if (result.Rows.Count > 0)
        {
            result.Currency = result.Rows.First().Currency;
        }

        return result;
    }

    private static bool TryGetQueryRows(
        string responseContent,
        out List<(string Name, int Index)> columns,
        out List<JsonElement> rows)
    {
        columns = new List<(string Name, int Index)>();
        rows = new List<JsonElement>();

        using var doc = JsonDocument.Parse(responseContent);
        if (!doc.RootElement.TryGetProperty("properties", out var properties))
            return false;

        if (!properties.TryGetProperty("columns", out var columnsElement) ||
            !properties.TryGetProperty("rows", out var rowsElement))
            return false;

        columns = columnsElement
            .EnumerateArray()
            .Select((c, index) => (
                Name: c.GetProperty("name").GetString() ?? string.Empty,
                Index: index))
            .ToList();

        rows = rowsElement.EnumerateArray().Select(r => r.Clone()).ToList();
        return true;
    }

    private static string NormalizeGranularity(string granularity)
    {
        return string.Equals(granularity, "Daily", StringComparison.OrdinalIgnoreCase)
            ? "Daily"
            : "None";
    }

    private static int FindColumn(IEnumerable<(string Name, int Index)> columns, params string[] names)
    {
        foreach (var name in names)
        {
            var match = columns.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match != default)
                return match.Index;
        }

        return -1;
    }

    private static string? ReadString(JsonElement row, int index)
    {
        if (index < 0 || index >= row.GetArrayLength())
            return null;

        var item = row[index];
        return item.ValueKind switch
        {
            JsonValueKind.String => item.GetString(),
            JsonValueKind.Number => item.GetRawText(),
            _ => item.ToString()
        };
    }

    private static decimal ReadDecimal(JsonElement row, int index)
    {
        if (index < 0 || index >= row.GetArrayLength())
            return 0m;

        var item = row[index];
        if (item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out var value))
            return value;

        if (item.ValueKind == JsonValueKind.String &&
            decimal.TryParse(item.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return 0m;
    }

    private static DateTime? ReadDate(JsonElement row, int index)
    {
        if (index < 0 || index >= row.GetArrayLength())
            return null;

        var item = row[index];
        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var yyyymmdd))
        {
            var asText = yyyymmdd.ToString(CultureInfo.InvariantCulture);
            if (DateTime.TryParseExact(asText, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
                return DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc);
        }

        if (item.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(item.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return date.ToUniversalTime();

        return null;
    }

    private static string ExtractResourceName(string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
            return "unknown";

        var normalized = resourceId.TrimEnd('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? normalized : parts[^1];
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var raw = values.FirstOrDefault();
            if (int.TryParse(raw, out var retryAfterSeconds))
            {
                return TimeSpan.FromSeconds(Math.Max(1, retryAfterSeconds));
            }
        }

        var baseDelayMs = Math.Pow(2, attempt) * 500;
        return TimeSpan.FromMilliseconds(baseDelayMs + Random.Shared.Next(200, 900));
    }
}
