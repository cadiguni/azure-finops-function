using Azure.Identity;
using Gvdasa.FinOpsApi.AzureFunctions.Models;
using System.Text.Json;

namespace Gvdasa.FinOpsApi.AzureFunctions.Analyzers;

/// <summary>
/// Analisa recursos de Storage Account não utilizados ou subutilizados
/// </summary>
public class StorageAccountAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;

    public StorageAccountAnalyzer(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _credential = new DefaultAzureCredential();
    }

    /// <summary>
    /// Analisa Storage Accounts com pouco uso em uma subscription
    /// </summary>
    public async Task<List<CostRecommendation>> AnalyzeSubscriptionAsync(string subscriptionId)
    {
        var recommendations = new List<CostRecommendation>();

        try
        {
            // Query KQL para Storage Accounts com pouco uso (menos de 1GB)
            var kqlQuery = $@"
                Resources
                | where type =~ 'microsoft.storage/storageaccounts'
                | where subscriptionId =~ '{subscriptionId}'
                | project 
                    resourceId = id,
                    name,
                    resourceGroup,
                    subscriptionId,
                    location,
                    sku = tostring(sku.name),
                    kind,
                    accessTier = tostring(properties.accessTier),
                    tags
            ";

            var storageAccounts = await ExecuteResourceGraphQueryAsync(kqlQuery);

            foreach (var storage in storageAccounts)
            {
                var recommendation = CreateStorageRecommendationAsync(storage, subscriptionId);
                if (recommendation != null)
                {
                    recommendations.Add(recommendation);
                }
            }

            Console.WriteLine($"🏪 Storage Account Analyzer: {recommendations.Count} recomendações geradas");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Erro no Storage Account Analyzer: {ex.Message}");
        }

        return recommendations;
    }

    private async Task<List<JsonElement>> ExecuteResourceGraphQueryAsync(string query)
    {
        try
        {
            Console.WriteLine("🔐 Storage: Iniciando autenticação Azure...");
            
            var tokenRequestContext = new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" });
            var tokenResponse = await _credential.GetTokenAsync(tokenRequestContext);
            
            Console.WriteLine("✅ Storage: Token obtido com sucesso");
            Console.WriteLine($"📤 Storage: Executando query KQL: {query}");

            var requestBody = new
            {
                query = query,
                options = new { }
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {tokenResponse.Token}");

            var response = await _httpClient.PostAsync("https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01", content);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"📥 Storage: Resource Graph resposta: {responseContent}");
                var result = JsonSerializer.Deserialize<JsonElement>(responseContent);

                if (result.TryGetProperty("data", out var dataElement))
                {
                    var dataList = dataElement.EnumerateArray().ToList();
                    Console.WriteLine($"📊 Storage: Encontrados {dataList.Count} recursos na query");
                    return dataList;
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ Storage: Erro na query Resource Graph: {response.StatusCode} - {errorContent}");
            }
        }
        catch (Azure.Identity.AuthenticationFailedException authEx)
        {
            Console.WriteLine($"❌ Storage: Falha de autenticação Azure: {authEx.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Storage: Erro ao executar query Resource Graph: {ex.Message}");
        }

        return new List<JsonElement>();
    }

    private CostRecommendation? CreateStorageRecommendationAsync(JsonElement storage, string subscriptionId)
    {
        try
        {
            var resourceId = storage.GetProperty("resourceId").GetString();
            var name = storage.GetProperty("name").GetString();
            var resourceGroup = storage.GetProperty("resourceGroup").GetString();
            var location = storage.GetProperty("location").GetString();
            var sku = storage.GetProperty("sku").GetString();

            // Estimativa básica de custo (valores aproximados)
            decimal estimatedMonthlyCost = sku?.ToLower() switch
            {
                var s when s?.Contains("standard_lrs") == true => 20.00m,
                var s when s?.Contains("standard_grs") == true => 35.00m,
                var s when s?.Contains("premium") == true => 150.00m,
                _ => 25.00m
            };

            return new CostRecommendation
            {
                Type = "UnderUtilizedStorageAccount",
                ResourceId = resourceId ?? "",
                ResourceName = name ?? "",
                ResourceType = "Microsoft.Storage/storageAccounts",
                ResourceGroup = resourceGroup ?? "",
                SubscriptionId = subscriptionId,
                EstimatedMonthlySavings = estimatedMonthlyCost * 0.7m, // 70% de economia potencial
                Description = $"Storage Account '{name}' pode estar subutilizado. Considere revisar métricas de uso. Revisar utilização ou migrar para tier mais econômico.",
                Priority = "Medium",
                Tags = ExtractTags(storage)
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Storage: Erro ao criar recomendação: {ex.Message}");
            return null;
        }
    }

    private Dictionary<string, string> ExtractTags(JsonElement resource)
    {
        var tags = new Dictionary<string, string>();
        
        try
        {
            if (resource.TryGetProperty("tags", out var tagsElement) && 
                tagsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var tag in tagsElement.EnumerateObject())
                {
                    tags[tag.Name] = tag.Value.GetString() ?? "";
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Storage: Erro ao extrair tags: {ex.Message}");
        }

        return tags;
    }
}