using System.Text.Json;
using Azure.Identity;
using Gvdasa.FinOpsApi.AzureFunctions.Models;

namespace Gvdasa.FinOpsApi.AzureFunctions.Analyzers;

public class UnattachedDiskAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;

    public UnattachedDiskAnalyzer(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _credential = new DefaultAzureCredential();
    }

    /// <summary>
    /// Analisa discos não anexados em uma subscription
    /// </summary>
    public async Task<List<CostRecommendation>> AnalyzeSubscriptionAsync(string subscriptionId)
    {
        var recommendations = new List<CostRecommendation>();

        try
        {
            // Query KQL otimizada para buscar discos não anexados
            var kqlQuery = $@"
                Resources
                | where type =~ 'microsoft.compute/disks'
                | where subscriptionId =~ '{subscriptionId}'
                | where isnull(properties.managedBy) or properties.managedBy == """"
                | where properties.diskState =~ 'Unattached'
                | project 
                    resourceId = id,
                    name,
                    resourceGroup,
                    subscriptionId,
                    location,
                    sku = properties.sku.name,
                    diskSizeGb = properties.diskSizeGB,
                    diskState = properties.diskState,
                    timeCreated = properties.timeCreated,
                    tags
            ";

            var disks = await ExecuteResourceGraphQueryAsync(kqlQuery);

            foreach (var disk in disks)
            {
                var recommendation = await CreateDiskRecommendationAsync(disk);
                if (recommendation != null)
                {
                    recommendations.Add(recommendation);
                }
            }
        }
        catch (Exception ex)
        {
            // Log error but continue processing
            Console.WriteLine($"Erro ao analisar discos na subscription {subscriptionId}: {ex.Message}");
        }

        return recommendations;
    }

    /// <summary>
    /// Executa query no Azure Resource Graph
    /// </summary>
    private async Task<List<JsonElement>> ExecuteResourceGraphQueryAsync(string query)
    {
        try
        {
            Console.WriteLine("🔐 Iniciando autenticação Azure...");
            
            // Obter token de acesso
            var tokenRequestContext = new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" });
            var tokenResponse = await _credential.GetTokenAsync(tokenRequestContext);
            
            Console.WriteLine("✅ Token obtido com sucesso");

            // Preparar requisição para o Resource Graph API
            var requestBody = new
            {
                query = query,
                options = new { }
            };

            var json = JsonSerializer.Serialize(requestBody);
            Console.WriteLine($"📤 Executando query KQL: {query}");
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            // Configurar headers
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {tokenResponse.Token}");

            // Executar query
            var response = await _httpClient.PostAsync("https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01", content);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"📥 Resource Graph resposta: {responseContent}");
                var result = JsonSerializer.Deserialize<JsonElement>(responseContent);

                if (result.TryGetProperty("data", out var dataElement))
                {
                    var dataList = dataElement.EnumerateArray().ToList();
                    Console.WriteLine($"📊 Encontrados {dataList.Count} recursos na query");
                    return dataList;
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ Erro na query Resource Graph: {response.StatusCode} - {errorContent}");
            }
        }
        catch (Azure.Identity.AuthenticationFailedException authEx)
        {
            Console.WriteLine($"❌ Falha de autenticação Azure: {authEx.Message}");
            Console.WriteLine($"❌ Detalhes: {authEx}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Erro ao executar query Resource Graph: {ex.Message}");
        }

        return new List<JsonElement>();
    }

    /// <summary>
    /// Cria recomendação baseada nos dados do disco
    /// </summary>
    private Task<CostRecommendation?> CreateDiskRecommendationAsync(JsonElement disk)
    {
        try
        {
            var resourceId = disk.GetProperty("resourceId").GetString() ?? "";
            var name = disk.GetProperty("name").GetString() ?? "";
            var resourceGroup = disk.GetProperty("resourceGroup").GetString() ?? "";
            var subscriptionId = disk.GetProperty("subscriptionId").GetString() ?? "";
            var sku = disk.GetProperty("sku").GetString() ?? "";
            var diskSizeGb = disk.GetProperty("diskSizeGb").GetInt32();

            // Estimar custo baseado no tipo de disco e tamanho
            var estimatedMonthlyCost = EstimateDiskMonthlyCost(sku, diskSizeGb);

            // Parse tags
            var tags = new Dictionary<string, string>();
            if (disk.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var tag in tagsElement.EnumerateObject())
                {
                    tags[tag.Name] = tag.Value.GetString() ?? "";
                }
            }

            return Task.FromResult<CostRecommendation?>(new CostRecommendation
            {
                Type = "UNATTACHED_DISK",
                ResourceId = resourceId,
                ResourceName = name,
                ResourceType = "Microsoft.Compute/disks",
                ResourceGroup = resourceGroup,
                SubscriptionId = subscriptionId,
                EstimatedMonthlySavings = estimatedMonthlyCost,
                Description = $"Disco '{name}' ({sku}, {diskSizeGb}GB) não está anexado a nenhuma VM há mais tempo. Economia estimada: ${estimatedMonthlyCost:F2}/mês",
                Priority = estimatedMonthlyCost > 50 ? "High" : estimatedMonthlyCost > 20 ? "Medium" : "Low",
                Tags = tags
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro ao criar recomendação para disco: {ex.Message}");
            return Task.FromResult<CostRecommendation?>(null);
        }
    }

    /// <summary>
    /// Estima custo mensal baseado no SKU e tamanho do disco
    /// </summary>
    private decimal EstimateDiskMonthlyCost(string sku, int diskSizeGb)
    {
        // Preços aproximados por GB/mês (USD) - região East US
        var pricePerGbPerMonth = sku.ToLowerInvariant() switch
        {
            var s when s.Contains("premium") => 0.15m,  // Premium SSD
            var s when s.Contains("standard") && s.Contains("ssd") => 0.05m,  // Standard SSD
            var s when s.Contains("standard") => 0.045m,  // Standard HDD
            _ => 0.05m  // Default fallback
        };

        return diskSizeGb * pricePerGbPerMonth;
    }
}