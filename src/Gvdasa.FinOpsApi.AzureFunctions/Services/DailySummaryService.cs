using System.Text.Json;
using Azure.Storage.Blobs;
using Gvdasa.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gvdasa.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Serviço para agregar dados de custo e gerar summary diário
/// </summary>
public class DailySummaryService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DailySummaryService> _logger;
    
    private readonly JsonSerializerOptions _jsonOptions;

    public DailySummaryService(
        BlobServiceClient blobServiceClient,
        IConfiguration configuration,
        ILogger<DailySummaryService> logger)
    {
        _blobServiceClient = blobServiceClient;
        _configuration = configuration;
        _logger = logger;
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
    }

    /// <summary>
    /// Processa dados de um dia e gera summary consolidado
    /// </summary>
    public async Task<DailySummary> ProcessDayAsync(string date)
    {
        _logger.LogInformation("📊 Iniciando agregação diária para {date}", date);

        try
        {
            // 1. Ler todos os findings do dia
            var allFindings = await ReadAllFindingsAsync(date);
            _logger.LogInformation("📥 Carregados {count} cost findings", allFindings.Count);

            // 2. Calcular agregações
            var summary = await CalculateSummaryAsync(date, allFindings);
            
            // 3. Salvar summary
            await SaveSummaryAsync(summary);
            
            _logger.LogInformation("✅ Summary gerado: {totalSavings:C} de economia potencial", summary.TotalPotentialSavings);
            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao processar summary do dia {date}", date);
            throw;
        }
    }

    /// <summary>
    /// Lê todos os cost findings de um dia específico
    /// </summary>
    private async Task<List<CostFinding>> ReadAllFindingsAsync(string date)
    {
        var allFindings = new List<CostFinding>();
        
        try
        {
            var containerName = _configuration["RESULTS_CONTAINER_NAME"] ?? "cost-analysis";
            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            
            // 🎯 FASE B - Usar BlobPathBuilder padronizado para analyses/
            var analysisDate = DateTime.ParseExact(date, "yyyy-MM-dd", null);
            var prefix = BlobPathBuilder.BuildAnalysesDailyPrefix(analysisDate);
            
            _logger.LogInformation("🔍 Buscando análises com prefixo: {prefix}", prefix);

            await foreach (var blobItem in containerClient.GetBlobsAsync(prefix: prefix))
            {
                try
                {
                    var blobClient = containerClient.GetBlobClient(blobItem.Name);
                    var content = await blobClient.DownloadContentAsync();
                    var contentStr = content.Value.Content.ToString();
                    
                    // Tentar deserializar como FinOpsAnalysisResult (formato atual)
                    var analysisResult = JsonSerializer.Deserialize<FinOpsAnalysisResult>(contentStr, _jsonOptions);
                    
                    if (analysisResult?.Recommendations != null)
                    {
                        // Converter recomendações para CostFinding
                        foreach (var rec in analysisResult.Recommendations)
                        {
                            var finding = new CostFinding
                            {
                                ResourceId = rec.ResourceId,
                                ResourceType = rec.Type,
                                ResourceName = rec.ResourceName,
                                SubscriptionId = rec.SubscriptionId,
                                ResourceGroup = rec.ResourceGroup,
                                EstimatedMonthlyCost = rec.EstimatedMonthlySavings / 0.7m, // Reverse engineer do custo
                                PotentialSavings = rec.EstimatedMonthlySavings,
                                Confidence = rec.Priority == "High" ? "High" : "Medium",
                                Priority = rec.Priority,
                                Description = rec.Description
                            };
                            
                            allFindings.Add(finding);
                        }
                        
                        _logger.LogDebug("📄 Processado blob: {name} ({count} findings)", blobItem.Name, analysisResult.Recommendations.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Erro ao processar blob {name}", blobItem.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao ler findings do dia {date}", date);
        }

        return allFindings;
    }

    /// <summary>
    /// Calcula todas as agregações do summary
    /// </summary>
    private async Task<DailySummary> CalculateSummaryAsync(string date, List<CostFinding> findings)
    {
        var summary = new DailySummary
        {
            Date = date,
            GeneratedAt = DateTime.UtcNow,
            TotalResourcesAnalyzed = findings.Count,
            TotalPotentialSavings = findings.Sum(f => f.PotentialSavings)
        };

        // Agregação por tipo de recurso
        var byType = findings
            .GroupBy(f => f.ResourceType)
            .ToDictionary(
                g => g.Key,
                g => new DailySummaryByType
                {
                    Count = g.Count(),
                    PotentialSavings = g.Sum(f => f.PotentialSavings)
                });
        
        summary.SummaryByType = byType;

        // Agregação por subscription
        var bySubscription = findings
            .GroupBy(f => f.SubscriptionId)
            .ToDictionary(
                g => g.Key,
                g => new DailySummaryBySubscription
                {
                    SubscriptionId = g.Key,
                    Count = g.Count(),
                    PotentialSavings = g.Sum(f => f.PotentialSavings)
                });
        
        summary.SummaryBySubscription = bySubscription;

        // Top 10 por economia potencial
        summary.Top10 = findings
            .OrderByDescending(f => f.PotentialSavings)
            .Take(10)
            .ToList();

        _logger.LogInformation("📈 Calculado summary: {types} tipos, {subs} subscriptions, Top savings: {topSaving:C}", 
            byType.Count, bySubscription.Count, summary.Top10.FirstOrDefault()?.PotentialSavings ?? 0);

        return await Task.FromResult(summary);
    }

    /// <summary>
    /// Salva o summary no blob storage
    /// </summary>
    private async Task SaveSummaryAsync(DailySummary summary)
    {
        try
        {
            var containerName = _configuration["RESULTS_CONTAINER_NAME"] ?? "cost-analysis";
            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            await containerClient.CreateIfNotExistsAsync();

            // 🎯 FASE B - Path padronizado para summary
            var summaryDate = DateTime.ParseExact(summary.Date, "yyyy-MM-dd", null);
            var blobName = BlobPathBuilder.BuildDailySummaryPath(summaryDate);
            
            var blobClient = containerClient.GetBlobClient(blobName);
            
            var json = JsonSerializer.Serialize(summary, _jsonOptions);
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            
            await blobClient.UploadAsync(stream, overwrite: true);
            
            _logger.LogInformation("💾 Summary salvo em: {blobName}", blobName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao salvar summary");
            throw;
        }
    }
}