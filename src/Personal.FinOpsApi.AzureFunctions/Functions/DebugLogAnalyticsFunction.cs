using System.Net;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Services;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// Endpoint de debug para listar workspaces do Log Analytics.
/// </summary>
public class DebugLogAnalyticsFunction
{
    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;
    private readonly HttpRetryService _httpRetryService;
    private readonly ILogger<DebugLogAnalyticsFunction> _logger;

    public DebugLogAnalyticsFunction(
        IHttpClientFactory httpClientFactory, 
        HttpRetryService httpRetryService,
        ILogger<DebugLogAnalyticsFunction> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _credential = new DefaultAzureCredential();
        _httpRetryService = httpRetryService;
        _logger = logger;
    }

    /// <summary>
    /// Lista todos os workspaces do Log Analytics na subscription.
    /// GET /api/debug/loganalytics?subscriptionId=xxx
    /// </summary>
    [Function("debug-loganalytics")]
    public async Task<HttpResponseData> ListLogAnalyticsWorkspaces(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "debug/loganalytics")] HttpRequestData req)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var subscriptionId = query["subscriptionId"] ?? Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");

            if (string.IsNullOrEmpty(subscriptionId))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteAsJsonAsync(new 
                { 
                    error = "Parâmetro subscriptionId é obrigatório ou configure AZURE_SUBSCRIPTION_ID" 
                });
                return badResponse;
            }

            _logger.LogInformation("[DEBUG-LOGANALYTICS] Listando workspaces para subscription {subscriptionId}", subscriptionId);

            // Usa Resource Graph para listar workspaces (mesmo approach do LogAnalyticsAnalyzer)
            var workspaces = await ListWorkspacesViaResourceGraphAsync(subscriptionId);

            _logger.LogInformation("[DEBUG-LOGANALYTICS] Encontrados {count} workspaces", workspaces.Count);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                subscriptionId,
                count = workspaces.Count,
                timestamp = DateTime.UtcNow,
                workspaces,
                analysisRules = new
                {
                    highRetentionThreshold = "retentionInDays > 30 (default é 30)",
                    lowIngestionThreshold = "dailyIngestion < 0.01 GB",
                    highIngestionThreshold = "dailyIngestion > 5 GB",
                    note = "Se retention = 30 (default) e ingestion normal, nenhum finding será gerado"
                }
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DEBUG-LOGANALYTICS] Erro: {error}", ex.Message);
            
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new 
            { 
                error = "Erro interno ao listar workspaces de Log Analytics."
            });
            return errorResponse;
        }
    }

    private async Task<List<object>> ListWorkspacesViaResourceGraphAsync(string subscriptionId)
    {
        var kqlQuery = $@"
            Resources
            | where type =~ 'microsoft.operationalinsights/workspaces'
            | where subscriptionId =~ '{subscriptionId}'
            | project
                resourceId = id,
                name,
                resourceGroup,
                subscriptionId,
                location,
                workspaceId = properties.customerId,
                retentionInDays = properties.retentionInDays,
                sku = properties.sku.name,
                features = properties.features,
                publicNetworkAccess = properties.publicNetworkAccessForIngestion,
                createdDate = properties.createdDate,
                modifiedDate = properties.modifiedDate,
                tags
        ";

        var token = await _credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }));

        var resourceGraphPayload = new { query = kqlQuery };
        var jsonPayload = JsonSerializer.Serialize(resourceGraphPayload);
        var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");

        var httpResponse = await _httpRetryService.PostWithRetryAsync(
            _httpClient,
            "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01",
            content);

        if (!httpResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("[DEBUG-LOGANALYTICS] Resource Graph API retornou {status}", httpResponse.StatusCode);
            return new List<object>();
        }

        var jsonResponse = await httpResponse.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(jsonResponse);
        var results = new List<object>();

        foreach (var workspace in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            results.Add(new
            {
                Name = workspace.TryGetProperty("name", out var n) ? n.GetString() : null,
                ResourceGroup = workspace.TryGetProperty("resourceGroup", out var rg) ? rg.GetString() : null,
                Location = workspace.TryGetProperty("location", out var loc) ? loc.GetString() : null,
                WorkspaceId = workspace.TryGetProperty("workspaceId", out var wid) ? wid.GetString() : null,
                ResourceId = workspace.TryGetProperty("resourceId", out var rid) ? rid.GetString() : null,
                Sku = workspace.TryGetProperty("sku", out var sku) ? sku.GetString() : null,
                RetentionInDays = workspace.TryGetProperty("retentionInDays", out var ret) && ret.ValueKind == JsonValueKind.Number ? ret.GetInt32() : 30,
                PublicNetworkAccess = workspace.TryGetProperty("publicNetworkAccess", out var pna) ? pna.GetString() : null,
                Tags = workspace.TryGetProperty("tags", out var tags) ? tags.ToString() : null
            });
        }

        return results;
    }
}
