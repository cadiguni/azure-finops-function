using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
///  Serviço para agregar dados FinOps para consumo direto pelo Grafana
/// Lê blobs brutos → Agrega → Retorna JSON tabular
/// </summary>
public class GrafanaDataService
{
    private readonly AnalysisStorageService _storageService;
    private readonly ILogger<GrafanaDataService> _logger;

    public GrafanaDataService(
        AnalysisStorageService storageService,
        ILogger<GrafanaDataService> logger)
    {
        _storageService = storageService;
        _logger = logger;
    }

    /// <summary>
    ///  Agrega economias por tipo de recurso - formato otimizado para Grafana
    /// Retorna: [{ "label": "App Service Plan", "totalSavings": 6613, "count": 12 }]
    /// </summary>
    public async Task<List<GrafanaResourceTypeAggregation>> GetSavingsByResourceTypeAsync(string date, string subscriptionFilter = "all")
    {
        _logger.LogInformation(" Agregando economias por tipo de recurso para {date}", date);

        try
        {
            // 1. Usar o método existente do AnalysisStorageService
            var analysisDate = DateTime.ParseExact(date, "yyyy-MM-dd", null);
            var allRecommendations = await _storageService.GetDailyAnalysisAsync(analysisDate);
            
            _logger.LogInformation(" Carregadas {count} recommendations", allRecommendations.Count);

            // 2. Filtrar por subscription se especificado
            if (subscriptionFilter != "all" && !string.IsNullOrEmpty(subscriptionFilter))
            {
                allRecommendations = allRecommendations.Where(f => f.SubscriptionId == subscriptionFilter).ToList();
                _logger.LogInformation(" Filtrado para subscription {subscription}: {count} recommendations", subscriptionFilter, allRecommendations.Count);
            }

            // 3. Agrupar por tipo de recurso e agregar
            var aggregated = allRecommendations
                .GroupBy(f => f.ResourceType)
                .Select(group => new GrafanaResourceTypeAggregation
                {
                    Label = FormatResourceTypeLabel(group.Key),
                    TotalSavings = group.Sum(f => f.PotentialMonthlySavings),
                    Count = group.Count(),
                    ResourceType = group.Key
                })
                .OrderByDescending(a => a.TotalSavings)
                .ToList();

            _logger.LogInformation(" Agregação concluída: {types} tipos de recurso", aggregated.Count);
            return aggregated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao agregar por tipo de recurso");
            return new List<GrafanaResourceTypeAggregation>();
        }
    }

    /// <summary>
    ///  Agrega economias por subscription - formato otimizado para Grafana
    /// </summary>
    public async Task<List<GrafanaSubscriptionAggregation>> GetSavingsBySubscriptionAsync(string date)
    {
        _logger.LogInformation(" Agregando economias por subscription para {date}", date);

        try
        {
            var analysisDate = DateTime.ParseExact(date, "yyyy-MM-dd", null);
            var allRecommendations = await _storageService.GetDailyAnalysisAsync(analysisDate);

            var aggregated = allRecommendations
                .GroupBy(f => f.SubscriptionId)
                .Select(group => new GrafanaSubscriptionAggregation
                {
                    SubscriptionId = group.Key,
                    Label = GetSubscriptionLabel(group.Key), // Você pode mapear IDs para nomes amigáveis
                    TotalSavings = group.Sum(f => f.PotentialMonthlySavings),
                    Count = group.Count(),
                    ResourceTypes = group.GroupBy(f => f.ResourceType).Count()
                })
                .OrderByDescending(a => a.TotalSavings)
                .ToList();

            _logger.LogInformation(" Agregação por subscription concluída: {subs} subscriptions", aggregated.Count);
            return aggregated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao agregar por subscription");
            return new List<GrafanaSubscriptionAggregation>();
        }
    }

    /// <summary>
    ///  Retorna detalhes individuais por recurso - formato Grafana
    /// Formato: [{ "subscriptionId": "...", "resourceType": "AppServicePlan", "recommendation": "Underutilized", ... }]
    /// </summary>
    public async Task<List<GrafanaResourceDetail>> GetResourceDetailsAsync(string date, string subscriptionFilter = "all", string resourceTypeFilter = "all")
    {
        _logger.LogInformation(" Obtendo detalhes de recursos para {date}", date);

        try
        {
            var analysisDate = DateTime.ParseExact(date, "yyyy-MM-dd", null);
            var allRecommendations = await _storageService.GetDailyAnalysisAsync(analysisDate);

            // Filtros
            if (subscriptionFilter != "all" && !string.IsNullOrEmpty(subscriptionFilter))
            {
                allRecommendations = allRecommendations.Where(f => f.SubscriptionId == subscriptionFilter).ToList();
            }

            if (resourceTypeFilter != "all" && !string.IsNullOrEmpty(resourceTypeFilter))
            {
                allRecommendations = allRecommendations.Where(f => f.ResourceType.Equals(resourceTypeFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // Converter para formato Grafana
            var details = allRecommendations.Select(f => new GrafanaResourceDetail
            {
                SubscriptionId = f.SubscriptionId,
                ResourceType = f.ResourceType,
                ResourceName = f.ResourceName,
                ResourceId = f.ResourceId,
                Recommendation = f.Type,
                Confidence = f.Priority,
                Currency = "BRL", // ou pegar da configuração
                EstimatedSavings = f.PotentialMonthlySavings,
                Date = date,
                Description = f.Description
            }).ToList();

            _logger.LogInformation(" Detalhes obtidos: {count} recursos", details.Count);
            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao obter detalhes de recursos");
            return new List<GrafanaResourceDetail>();
        }
    }

    /// <summary>
    ///  Formata labels amigáveis para tipos de recurso
    /// </summary>
    private string FormatResourceTypeLabel(string resourceType)
    {
        return resourceType switch
        {
            "Microsoft.Web/serverfarms" => "App Service Plan",
            "Microsoft.Storage/storageAccounts" => "Storage Account",
            "Microsoft.Compute/virtualMachines" => "Virtual Machine",
            "Microsoft.Network/publicIPAddresses" => "Public IP Address",
            "Microsoft.Compute/disks" => "Managed Disk",
            _ => resourceType.Split('/').LastOrDefault() ?? resourceType
        };
    }

    /// <summary>
    ///  Obtém label amigável para subscription (pode ser expandido com mapeamento)
    /// </summary>
    private string GetSubscriptionLabel(string subscriptionId)
    {
        // Aqui você pode implementar um mapeamento de ID para nome amigável
        // Por enquanto, retorna os últimos 8 caracteres do ID
        return subscriptionId.Length > 8 ? $"...{subscriptionId.Substring(subscriptionId.Length - 8)}" : subscriptionId;
    }

    /// <summary>
    ///  Debug - Lista blobs disponíveis para uma data específica
    /// </summary>
    public async Task<object> DebugBlobsForDateAsync(DateTime date)
    {
        _logger.LogInformation(" Iniciando debug para data: {date}", date.ToString("yyyy-MM-dd"));

        try
        {
            // 1. Testar prefix building
            var expectedPrefix = $"analyses/year={date:yyyy}/month={date:MM}/day={date:dd}/";
            _logger.LogInformation(" Prefix esperado: {prefix}", expectedPrefix);

            // 2. Listar subscriptions
            var subscriptions = await _storageService.ListSubscriptionsByDateAsync(date);
            _logger.LogInformation(" Subscriptions encontradas: {count}", subscriptions.Count);

            // 3. DEBUG DETALHADO - Listar todos os blobs com o prefix
            var allBlobs = new List<string>();
            var recommendationBlobs = new List<string>();
            var errors = new List<string>();
            
            // Acessar o container diretamente para debug
            var containerClient = _storageService.GetContainerClient();
            await foreach (var blob in containerClient.GetBlobsAsync(prefix: expectedPrefix))
            {
                allBlobs.Add(blob.Name);
                
                if (blob.Name.EndsWith("recommendations.json"))
                {
                    recommendationBlobs.Add(blob.Name);
                }
            }

            // 4. Tentar carregar análises com debug
            var recommendations = new List<object>();
            try 
            {
                var loadedRecommendations = await _storageService.GetDailyAnalysisAsync(date);
                _logger.LogInformation(" Recommendations carregadas via GetDailyAnalysisAsync: {count}", loadedRecommendations.Count);
                
                recommendations = loadedRecommendations.Take(2).Cast<object>().ToList(); // Primeiros 2 para debug
            }
            catch (Exception ex)
            {
                errors.Add($"Erro em GetDailyAnalysisAsync: {ex.Message}");
                _logger.LogError(ex, "Erro ao carregar recommendations");
            }

            // 5. Informações dos primeiros recommendations (se existir)
            var sampleRecommendation = recommendations.FirstOrDefault();

            return new
            {
                Date = date.ToString("yyyy-MM-dd"),
                ExpectedPrefix = expectedPrefix,
                SubscriptionsFound = subscriptions.Count,
                Subscriptions = subscriptions.Take(5).ToList(), // Primeiros 5
                AllBlobsFound = allBlobs.Count,
                SampleBlobs = allBlobs.Take(10).ToList(), // Primeiros 10 para inspeção
                RecommendationBlobsFound = recommendationBlobs.Count,
                RecommendationBlobs = recommendationBlobs.Take(5).ToList(),
                RecommendationsLoaded = recommendations.Count,
                SampleRecommendation = sampleRecommendation,
                Errors = errors
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro no debug");
            return new
            {
                Date = date.ToString("yyyy-MM-dd"),
                Error = ex.Message,
                StackTrace = ex.StackTrace
            };
        }
    }

    /// <summary>
    ///  Debug específico - Tenta ler um blob específico diretamente
    /// </summary>
    public async Task<object> DebugSpecificBlobAsync(DateTime date, string subscriptionId)
    {
        _logger.LogInformation(" Testando leitura específica de blob para subscription: {subscription}", subscriptionId);

        try
        {
            // Construir o path exato do blob
            var blobPath = $"analyses/year={date:yyyy}/month={date:MM}/day={date:dd}/{subscriptionId}/recommendations.json";
            _logger.LogInformation(" Path do blob: {path}", blobPath);

            // Acessar diretamente
            var containerClient = _storageService.GetContainerClient();
            var blobClient = containerClient.GetBlobClient(blobPath);

            // Verificar se existe
            var exists = await blobClient.ExistsAsync();
            _logger.LogInformation(" Blob existe: {exists}", exists.Value);

            if (!exists.Value)
            {
                return new
                {
                    BlobPath = blobPath,
                    Exists = false,
                    Message = "Blob não encontrado"
                };
            }

            // Ler propriedades
            var properties = await blobClient.GetPropertiesAsync();
            _logger.LogInformation(" Tamanho: {size} bytes", properties.Value.ContentLength);

            // Ler conteúdo
            var response = await blobClient.DownloadStreamingAsync();
            using var reader = new StreamReader(response.Value.Content);
            var content = await reader.ReadToEndAsync();

            _logger.LogInformation(" Conteúdo length: {length}", content.Length);

            // Tentar deserializar como List<CostRecommendation>
            object listDeserializationResult;
            try
            {
                var recommendations = System.Text.Json.JsonSerializer.Deserialize<List<CostRecommendation>>(content);
                listDeserializationResult = new
                {
                    Success = true,
                    Count = recommendations?.Count ?? 0,
                    FirstItem = recommendations?.FirstOrDefault()
                };
            }
            catch (Exception ex)
            {
                listDeserializationResult = new
                {
                    Success = false,
                    Error = ex.Message
                };
            }

            // Tentar deserializar como objeto genérico para ver a estrutura
            object genericDeserializationResult;
            try
            {
                var genericObject = System.Text.Json.JsonSerializer.Deserialize<object>(content);
                genericDeserializationResult = new
                {
                    Success = true,
                    Type = genericObject?.GetType()?.Name
                };
            }
            catch (Exception ex)
            {
                genericDeserializationResult = new
                {
                    Success = false,
                    Error = ex.Message
                };
            }

            return new
            {
                BlobPath = blobPath,
                Exists = true,
                SizeBytes = properties.Value.ContentLength,
                FullContent = content, // Mostrar conteúdo completo
                ListDeserialization = listDeserializationResult,
                GenericDeserialization = genericDeserializationResult
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro no debug específico");
            return new
            {
                Error = ex.Message,
                StackTrace = ex.StackTrace
            };
        }
    }
}

/// <summary>
///  Modelo de dados agregados por tipo de recurso para Grafana
/// </summary>
public class GrafanaResourceTypeAggregation
{
    public string Label { get; set; } = string.Empty;
    public decimal TotalSavings { get; set; }
    public int Count { get; set; }
    public string ResourceType { get; set; } = string.Empty;
}

/// <summary>
///  Modelo de dados agregados por subscription para Grafana
/// </summary>
public class GrafanaSubscriptionAggregation
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public decimal TotalSavings { get; set; }
    public int Count { get; set; }
    public int ResourceTypes { get; set; }
}

/// <summary>
///  Modelo de dados detalhados por recurso para Grafana
/// </summary>
public class GrafanaResourceDetail
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;
    public string Currency { get; set; } = "BRL";
    public decimal EstimatedSavings { get; set; }
    public string Date { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}