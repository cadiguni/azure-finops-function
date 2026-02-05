using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// 🗄️ FASE B - Padronização completa para Blob Storage
/// Padrão único: year=YYYY/month=MM/day=DD/subscription=XXXX/arquivo.json
/// </summary>
public class AnalysisStorageService
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<AnalysisStorageService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public AnalysisStorageService(
        BlobServiceClient blobServiceClient, 
        ILogger<AnalysisStorageService> logger,
        IConfiguration configuration)
    {
        var containerName = configuration["RESULTS_CONTAINER_NAME"] ?? "finops-analysis";
        _container = blobServiceClient.GetBlobContainerClient(containerName);
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        
        InitializeContainerAsync().Wait();
    }

    private async Task InitializeContainerAsync()
    {
        try
        {
            await _container.CreateIfNotExistsAsync(PublicAccessType.None);
            _logger.LogInformation("✅ Container {container} inicializado", _container.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Erro ao criar container: {error} - continuando...", ex.Message);
        }
    }

    /// <summary>
    /// 🎯 FASE B - Método principal padronizado
    /// Salva apenas RECOMENDAÇÕES LIMPAS em: analyses/year=YYYY/month=MM/day=DD/subscription=XXXX/recommendations.json
    /// </summary>
    public async Task SaveAsync(
        string subscriptionId, 
        object analysisResult, 
        DateTime analysisDateUtc)
    {
        try
        {
            var blobPath = BlobPathBuilder.BuildAnalysisPath(
                analysisDateUtc,
                subscriptionId,
                BlobPathBuilder.FileNames.Recommendations);
            
            var blobClient = _container.GetBlobClient(blobPath);

            // ✨ DIFERENCIAL: Extrair apenas recommendations + summary limpo
            var cleanResult = ExtractRecommendationsOnly(analysisResult);

            // Serializar com encoding UTF-8 e caracteres especiais
            var json = JsonSerializer.Serialize(cleanResult, _jsonOptions);
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

            await blobClient.UploadAsync(stream, overwrite: true);

            _logger.LogInformation("💾 Recomendações limpas salvas: {path}", blobPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Erro ao salvar no Storage: {error} - Salvaria: {subscription}", 
                ex.Message, subscriptionId);
        }
    }

    /// <summary>
    /// 📋 Lista todas as subscriptions analisadas em uma data específica
    /// Busca por padrão: analyses/year=YYYY/month=MM/day=DD/subscription=*/
    /// </summary>
    public async Task<List<string>> ListSubscriptionsByDateAsync(DateTime date)
    {
        try
        {
            var prefix = BlobPathBuilder.BuildAnalysesDailyPrefix(date);
            
            var subscriptions = new List<string>();
            await foreach (var blob in _container.GetBlobsAsync(prefix: prefix))
            {
                // Extrair subscription ID do path: .../subscription=XXXX/arquivo.json
                var pathParts = blob.Name.Split('/');
                var subscriptionPart = pathParts.FirstOrDefault(p => p.StartsWith("subscription="));
                if (!string.IsNullOrEmpty(subscriptionPart))
                {
                    var subscriptionId = subscriptionPart.Substring("subscription=".Length);
                    if (!subscriptions.Contains(subscriptionId))
                    {
                        subscriptions.Add(subscriptionId);
                    }
                }
            }
            
            return subscriptions;
        }
        catch (Exception ex)
        {
            _logger.LogError("❌ Erro ao listar subscriptions: {error}", ex.Message);
            return new List<string>();
        }
    }

    /// <summary>
    /// 📥 Carrega análise específica de uma subscription em uma data
    /// </summary>
    public async Task<List<CostRecommendation>> GetAnalysisAsync(DateTime date, string subscriptionId)
    {
        try
        {
            var blobPath = BlobPathBuilder.BuildAnalysisPath(
                date,
                subscriptionId,
                BlobPathBuilder.FileNames.Recommendations);
            
            var blobClient = _container.GetBlobClient(blobPath);

            if (!await blobClient.ExistsAsync())
            {
                _logger.LogWarning("📄 Blob não encontrado: {path}", blobPath);
                return new List<CostRecommendation>();
            }

            var response = await blobClient.DownloadStreamingAsync();
            using var reader = new StreamReader(response.Value.Content);
            var json = await reader.ReadToEndAsync();
            
            var recommendations = JsonSerializer.Deserialize<List<CostRecommendation>>(json, _jsonOptions);
            return recommendations ?? new List<CostRecommendation>();
        }
        catch (Exception ex)
        {
            _logger.LogError("❌ Erro ao carregar análise: {error}", ex.Message);
            return new List<CostRecommendation>();
        }
    }

    /// <summary>
    /// 🗃️ Carrega todas as análises de um dia específico
    /// </summary>
    public async Task<List<CostRecommendation>> GetDailyAnalysisAsync(DateTime date)
    {
        try
        {
            var prefix = BlobPathBuilder.BuildAnalysesDailyPrefix(date);
            var allRecommendations = new List<CostRecommendation>();
            
            _logger.LogInformation("🔍 Buscando análises com prefixo: {prefix}", prefix);

            await foreach (var blob in _container.GetBlobsAsync(prefix: prefix))
            {
                // Apenas arquivos de recomendações, não raw-analysis
                if (blob.Name.EndsWith(BlobPathBuilder.FileNames.Recommendations))
                {
                    var blobClient = _container.GetBlobClient(blob.Name);
                    var response = await blobClient.DownloadStreamingAsync();
                    using var reader = new StreamReader(response.Value.Content);
                    var json = await reader.ReadToEndAsync();
                    
                    var recommendations = JsonSerializer.Deserialize<List<CostRecommendation>>(json, _jsonOptions);
                    if (recommendations != null)
                    {
                        allRecommendations.AddRange(recommendations);
                    }
                }
            }
            
            _logger.LogInformation("📥 Carregadas {count} cost findings", allRecommendations.Count);
            return allRecommendations;
        }
        catch (Exception ex)
        {
            _logger.LogError("❌ Erro ao carregar análises diárias: {error}", ex.Message);
            return new List<CostRecommendation>();
        }
    }

    /// <summary>
    /// 🎯 Extrai APENAS a lista de recomendações para recommendations.json
    /// Remove todos os metadados, deixa só as ações concretas
    /// </summary>
    private object ExtractRecommendationsOnly(object analysisResult)
    {
        // Se for FinOpsAnalysisResult, extrair apenas as recommendations
        var resultType = analysisResult.GetType();
        
        if (resultType.Name == "FinOpsAnalysisResult")
        {
            var recommendationsProperty = resultType.GetProperty("Recommendations");
            var recommendations = recommendationsProperty?.GetValue(analysisResult);
            
            // 🎯 APENAS a lista de recomendações - sem metadados!
            return recommendations ?? new List<object>();
        }

        // Fallback: retorna original se não for FinOpsAnalysisResult
        return analysisResult;
    }
}
