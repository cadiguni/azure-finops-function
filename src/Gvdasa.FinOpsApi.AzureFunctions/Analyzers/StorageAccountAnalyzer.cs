using Azure.Identity;
using Gvdasa.FinOpsApi.AzureFunctions.Models;
using Gvdasa.FinOpsApi.AzureFunctions.Services;
using System.Text.Json;

namespace Gvdasa.FinOpsApi.AzureFunctions.Analyzers;

/// <summary>
/// ANALYZER v2.0 - Segue contrato padrão StandardAnalyzerResult
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
    /// Analisa Storage Accounts seguindo o contrato padrão v1.0
    /// </summary>
    public async Task<StandardAnalyzerResult> AnalyzeSubscriptionAsync(string subscriptionId, int analysisPeriodDays = 30, bool dryRun = true)
    {
        var analysisId = Guid.NewGuid().ToString();
        var result = new StandardAnalyzerResult
        {
            AnalysisId = analysisId,
            Analyzer = AnalyzerNames.STORAGE_ACCOUNT_ANALYZER,
            SubscriptionId = subscriptionId,
            ExecutedAt = DateTime.UtcNow,
            AnalysisPeriodDays = analysisPeriodDays,
            DryRun = dryRun,
            ExecutionMetadata = new Dictionary<string, object>
            {
                { "queryExecutions", 0 },
                { "resourcesAnalyzed", 0 }
            }
        };

        try
        {
            Console.WriteLine($"🔍 {AnalyzerNames.STORAGE_ACCOUNT_ANALYZER}: Iniciando análise para subscription {subscriptionId}");

            // Query KQL para Storage Accounts
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
            result.ExecutionMetadata["queryExecutions"] = 1;
            result.ExecutionMetadata["resourcesAnalyzed"] = storageAccounts.Count;

            foreach (var storage in storageAccounts)
            {
                var finding = CreateStorageFinding(storage);
                if (finding != null)
                {
                    result.Findings.Add(finding);
                }
            }

            Console.WriteLine($"🏪 {AnalyzerNames.STORAGE_ACCOUNT_ANALYZER}: {result.Findings.Count} findings gerados");

            // Validar contrato antes de retornar
            var (isValid, errors) = AnalyzerContractValidator.ValidateResult(result);
            if (!isValid)
            {
                Console.WriteLine($"❌ CONTRATO INVÁLIDO: {string.Join(", ", errors)}");
                throw new InvalidOperationException($"Analyzer não segue o contrato padrão: {string.Join(", ", errors)}");
            }

            Console.WriteLine($"✅ CONTRATO VÁLIDO: {result.Findings.Count} findings");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Erro no {AnalyzerNames.STORAGE_ACCOUNT_ANALYZER}: {ex.Message}");
            
            // Mesmo com erro, retorna resultado válido
            result.ExecutionMetadata["error"] = ex.Message;
        }

        return result;
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

    /// <summary>
    /// Cria um finding padrão para Storage Account seguindo contrato v1.0
    /// </summary>
    private StandardFinding? CreateStorageFinding(JsonElement storage)
    {
        try
        {
            var resourceId = storage.GetProperty("resourceId").GetString();
            var name = storage.GetProperty("name").GetString();
            var resourceGroup = storage.GetProperty("resourceGroup").GetString();
            var location = storage.GetProperty("location").GetString();
            var sku = storage.GetProperty("sku").GetString();
            var subscriptionId = storage.GetProperty("subscriptionId").GetString();
            var kind = storage.GetProperty("kind").GetString();
            var accessTier = storage.GetProperty("accessTier").GetString();

            // Estimativa de custo baseada no SKU
            decimal estimatedMonthlyCost = sku?.ToLower() switch
            {
                var s when s?.Contains("standard_lrs") == true => 20.00m,
                var s when s?.Contains("standard_grs") == true => 35.00m,
                var s when s?.Contains("standard_zrs") == true => 28.00m,
                var s when s?.Contains("premium") == true => 150.00m,
                _ => 25.00m
            };

            var finding = new StandardFinding
            {
                Type = FindingTypes.UNDER_UTILIZED_STORAGE_ACCOUNT,
                ResourceId = resourceId ?? "",
                ResourceName = name ?? "",
                ResourceType = "Microsoft.Storage/storageAccounts",
                ResourceGroup = resourceGroup ?? "",
                SubscriptionId = subscriptionId ?? "",
                Location = location ?? "",
                EstimatedMonthlyCost = estimatedMonthlyCost,
                EstimatedMonthlySavings = estimatedMonthlyCost * 0.7m, // 70% de economia potencial
                Currency = "BRL",
                Priority = FindingPriorities.MEDIUM,
                Confidence = 0.6, // Confiança média pois não temos métricas de uso real
                Description = $"Storage Account '{name}' pode estar subutilizado. Considere revisar métricas de uso. Revisar utilização ou migrar para tier mais econômico.",
                Recommendation = "Avaliar métricas de uso dos últimos 30 dias e considerar migração para tier mais econômico ou exclusão se não utilizado.",
                Metadata = new Dictionary<string, object>
                {
                    { "sku", sku ?? "" },
                    { "kind", kind ?? "" },
                    { "accessTier", accessTier ?? "" },
                    { "estimationModel", "sku-based-fixed" },
                    { "potentialSavingsPercentage", 0.7 }
                }
            };

            // Processar tags do Azure
            if (storage.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var tag in tagsElement.EnumerateObject())
                {
                    finding.Tags[tag.Name] = tag.Value.GetString() ?? "";
                }
            }

            return finding;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Erro ao criar finding para storage: {ex.Message}");
            return null;
        }
    }
}