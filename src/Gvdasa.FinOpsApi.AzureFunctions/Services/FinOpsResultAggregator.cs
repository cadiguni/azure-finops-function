using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Gvdasa.FinOpsApi.AzureFunctions.Models;

namespace Gvdasa.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Agregador de resultados FinOps - salva análises históricas em Blob Storage
/// </summary>
public class FinOpsResultAggregator
{
    private readonly BlobContainerClient _containerClient;
    private readonly ILogger<FinOpsResultAggregator> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public FinOpsResultAggregator(IConfiguration configuration, ILogger<FinOpsResultAggregator> logger)
    {
        _logger = logger;
        
        // Configuração JSON com encoding correto para português
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        try
        {
            var connectionString = configuration.GetConnectionString("AzureWebJobsStorage") 
                                 ?? configuration["AzureWebJobsStorage"] 
                                 ?? "UseDevelopmentStorage=true";
            
            var containerName = configuration["RESULTS_CONTAINER_NAME"] ?? "finops-results";

            _containerClient = new BlobContainerClient(connectionString, containerName);
            
            // NÃO criar container automaticamente aqui - só quando salvar
            _logger.LogInformation("📦 FinOps Result Aggregator inicializado - Container: {container}", containerName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Storage não disponível, funcionando em modo somente-log");
            _containerClient = null!; // Indica que storage não está disponível
        }
    }

    /// <summary>
    /// Salva resultado da análise FinOps no Blob Storage
    /// </summary>
    public async Task SaveAnalysisResultAsync(FinOpsAnalysisResult result)
    {
        try
        {
            _logger.LogInformation("💾 Processando resultado FinOps: {analysisId}", result.AnalysisId);

            // 🧩 Estrutura de particionamento otimizada para produção
            // year=2026/month=02/day=02/subscription=abc.../analysisId=xyz.json
            var blobName = $"year={result.ExecutedAt:yyyy}/" +
                          $"month={result.ExecutedAt:MM}/" +
                          $"day={result.ExecutedAt:dd}/" +
                          $"subscription={result.SubscriptionId}/" +
                          $"analysisId={result.AnalysisId:N}.json";

            // Se storage não disponível, só loga o que salvaria
            if (_containerClient == null)
            {
                _logger.LogWarning("⚠️ Storage indisponível - SALVARIA: {blobName} ({recs} recomendações, R$ {savings}/mês)", 
                    blobName, result.Recommendations.Count, result.Summary.TotalEstimatedMonthlySavings);
                return;
            }

            // Garantir que container existe
            await _containerClient.CreateIfNotExistsAsync();
            
            var blobClient = _containerClient.GetBlobClient(blobName);

            // Serializar com encoding correto
            var jsonContent = JsonSerializer.Serialize(result, _jsonOptions);
            var jsonBytes = Encoding.UTF8.GetBytes(jsonContent);

            using var stream = new MemoryStream(jsonBytes);
            
            await blobClient.UploadAsync(stream, overwrite: true);

            _logger.LogInformation("✅ Resultado salvo: {blobName} ({size} bytes)", 
                blobName, jsonBytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao salvar resultado FinOps - continuando sem storage");
        }
    }

    /// <summary>
    /// Constrói resumo agregado das recomendações
    /// </summary>
    public static AnalysisSummary BuildSummary(List<CostRecommendation> recommendations)
    {
        var summary = new AnalysisSummary
        {
            TotalRecommendations = recommendations.Count,
            TotalEstimatedMonthlySavings = recommendations.Sum(r => r.EstimatedMonthlySavings)
        };

        // Agrupar por tipo
        var groupedByType = recommendations
            .GroupBy(r => r.Type)
            .ToDictionary(
                g => g.Key,
                g => new SummaryByType
                {
                    Count = g.Count(),
                    EstimatedMonthlySavings = g.Sum(r => r.EstimatedMonthlySavings)
                });

        summary.ByType = groupedByType;

        return summary;
    }

    /// <summary>
    /// Lista análises recentes (últimos 30 dias)
    /// </summary>
    public async Task<List<string>> ListRecentAnalysesAsync(string subscriptionId, int days = 30)
    {
        try
        {
            var results = new List<string>();
            var startDate = DateTime.UtcNow.AddDays(-days);

            await foreach (var blobItem in _containerClient.GetBlobsAsync(prefix: startDate.ToString("yyyy/MM")))
            {
                if (blobItem.Name.Contains(subscriptionId))
                {
                    results.Add(blobItem.Name);
                }
            }

            return results.OrderByDescending(x => x).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Erro ao listar análises recentes");
            return new List<string>();
        }
    }
}