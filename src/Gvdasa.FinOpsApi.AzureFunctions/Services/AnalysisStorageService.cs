using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

namespace Gvdasa.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Serviço de Storage otimizado para FinOps - Estrutura data → subscription
/// </summary>
public class AnalysisStorageService
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<AnalysisStorageService> _logger;
    
    private const string ContainerName = "finops-analysis";
    
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public AnalysisStorageService(
        BlobServiceClient blobServiceClient,
        ILogger<AnalysisStorageService> logger)
    {
        _container = blobServiceClient.GetBlobContainerClient(ContainerName);
        _logger = logger;
        
        try 
        {
            _container.CreateIfNotExists();
            _logger.LogInformation("📦 Container '{containerName}' inicializado", ContainerName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Erro ao criar container: {error} - continuando...", ex.Message);
        }
    }

    /// <summary>
    /// Salva análise no formato: year=2026/month=02/day=02/subscriptions/subscription-id.json
    /// </summary>
    public async Task SaveAsync(
        string subscriptionId, 
        object analysisResult, 
        DateTime analysisDateUtc)
    {
        try
        {
            var year = analysisDateUtc.Year;
            var month = analysisDateUtc.Month.ToString("D2");
            var day = analysisDateUtc.Day.ToString("D2");

            // 🧩 Estrutura OPÇÃO B: data → subscription
            var blobPath = $"year={year}/month={month}/day={day}/subscriptions/{subscriptionId}.json";
            
            var blobClient = _container.GetBlobClient(blobPath);

            // Serializar com encoding UTF-8 e caracteres especiais
            var json = JsonSerializer.Serialize(analysisResult, _jsonOptions);
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

            await blobClient.UploadAsync(stream, overwrite: true);

            _logger.LogInformation("📦 Análise salva: {blobPath} ({size} bytes)", 
                blobPath, stream.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Erro ao salvar no Storage: {error} - Salvaria: {subscription}", 
                ex.Message, subscriptionId);
        }
    }

    /// <summary>
    /// Lista todas as subscriptions analisadas em uma data específica
    /// </summary>
    public async Task<List<string>> ListSubscriptionsByDateAsync(DateTime date)
    {
        try
        {
            var year = date.Year;
            var month = date.Month.ToString("D2");
            var day = date.Day.ToString("D2");
            
            var prefix = $"year={year}/month={month}/day={day}/subscriptions/";
            
            var subscriptions = new List<string>();
            await foreach (var blob in _container.GetBlobsAsync(prefix: prefix))
            {
                var fileName = Path.GetFileNameWithoutExtension(blob.Name.Split('/').Last());
                subscriptions.Add(fileName);
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
    /// Carrega análise de uma subscription específica
    /// </summary>
    public async Task<T?> LoadAsync<T>(string subscriptionId, DateTime date) where T : class
    {
        try
        {
            var year = date.Year;
            var month = date.Month.ToString("D2");
            var day = date.Day.ToString("D2");
            
            var blobPath = $"year={year}/month={month}/day={day}/subscriptions/{subscriptionId}.json";
            var blobClient = _container.GetBlobClient(blobPath);
            
            if (!await blobClient.ExistsAsync())
                return null;
                
            var response = await blobClient.DownloadContentAsync();
            var json = response.Value.Content.ToString();
            
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError("❌ Erro ao carregar análise: {error}", ex.Message);
            return null;
        }
    }
}