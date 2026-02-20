using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// 🗄️ FASE B - Padronização completa para Blob Storage
/// Padrão único: year=YYYY/month=MM/day=DD/XXXX/arquivo.json
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
    /// Salva apenas RECOMENDAÇÕES LIMPAS em: analyses/year=YYYY/month=MM/day=DD/XXXX/recommendations.json
    /// </summary>
    public async Task SaveAsync(
        string subscriptionId, 
        object analysisResult, 
        DateTime analysisDateUtc)
    {
        try
        {
            // 💾 1. SALVAR RECOMMENDATIONS (processadas)
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

            // 🗜 2. SALVAR RAW DATA (para debug/auditoria)
            await SaveRawAsync(subscriptionId, analysisResult, analysisDateUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Erro ao salvar no Storage: {error} - Salvaria: {subscription}", 
                ex.Message, subscriptionId);
        }
    }

    /// <summary>
    /// 🗜 DADOS RAW - Salva dados brutos para debug/auditoria
    /// Caminho: raw-analysis/year=YYYY/month=MM/day=DD/XXXX/raw-findings.json
    /// </summary>
    public async Task SaveRawAsync(
        string subscriptionId, 
        object analysisResult, 
        DateTime analysisDateUtc)
    {
        try
        {
            var rawBlobPath = $"raw-analysis/year={analysisDateUtc:yyyy}/month={analysisDateUtc:MM}/day={analysisDateUtc:dd}/{subscriptionId}/raw-findings.json";
            var rawBlobClient = _container.GetBlobClient(rawBlobPath);

            // Salvar dados brutos completos (sem processamento)
            var rawJson = JsonSerializer.Serialize(analysisResult, _jsonOptions);
            using var rawStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawJson));

            await rawBlobClient.UploadAsync(rawStream, overwrite: true);
            _logger.LogInformation("🗜 Dados RAW salvos: {path}", rawBlobPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Erro ao salvar RAW: {error}", ex.Message);
        }
    }

    /// <summary>
    /// 📋 Lista todas as subscriptions analisadas em uma data específica
    /// Busca por padrão: analyses/year=YYYY/month=MM/day=DD/*/
    /// </summary>
    public async Task<List<string>> ListSubscriptionsByDateAsync(DateTime date)
    {
        try
        {
            var prefix = BlobPathBuilder.BuildAnalysesDailyPrefix(date);
            
            var subscriptions = new List<string>();
            await foreach (var blob in _container.GetBlobsAsync(prefix: prefix))
            {
                // Extrair subscription ID do path: analyses/year=YYYY/month=MM/day=DD/SUBSCRIPTION_ID/arquivo.json
                var pathParts = blob.Name.Split('/');
                if (pathParts.Length >= 5) // Precisa de pelo menos 5 partes
                {
                    var subscriptionId = pathParts[4]; // Posição correta após day=DD
                    if (!string.IsNullOrEmpty(subscriptionId) && !subscriptions.Contains(subscriptionId))
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
    /// � Obtém o cliente do container para debug (uso interno)
    /// </summary>
    public BlobContainerClient GetContainerClient()
    {
        return _container;
    }

    /// <summary>
    /// �🗃️ Carrega todas as análises de um dia específico
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
                    try
                    {
                        _logger.LogInformation("🔍 Processando blob: {blobName}", blob.Name);
                        
                        var blobClient = _container.GetBlobClient(blob.Name);
                        
                        // Verificar se o blob existe e tem conteúdo
                        var properties = await blobClient.GetPropertiesAsync();
                        _logger.LogInformation("📏 Tamanho do blob: {size} bytes", properties.Value.ContentLength);
                        
                        if (properties.Value.ContentLength == 0)
                        {
                            _logger.LogWarning("⚠️ Blob vazio: {blobName}", blob.Name);
                            continue;
                        }
                        
                        var response = await blobClient.DownloadStreamingAsync();
                        using var reader = new StreamReader(response.Value.Content);
                        var json = await reader.ReadToEndAsync();
                        
                        _logger.LogInformation("📄 JSON length: {length}, starts with: {start}", 
                            json.Length, 
                            json.Length > 50 ? json.Substring(0, 50) : json);
                        
                        if (string.IsNullOrWhiteSpace(json))
                        {
                            _logger.LogWarning("⚠️ JSON vazio ou null: {blobName}", blob.Name);
                            continue;
                        }
                        
                        // 🔄 NOVA LÓGICA: Primeiro tentar como AnalysisResult completo
                        List<CostRecommendation> recommendations = null;
                        
                        try
                        {
                            var analysisResult = JsonSerializer.Deserialize<FullAnalysisResult>(json, _jsonOptions);
                            if (analysisResult?.Recommendations != null)
                            {
                                recommendations = analysisResult.Recommendations;
                                _logger.LogInformation("✅ Deserializado como FullAnalysisResult: {count} recommendations", recommendations.Count);
                            }
                        }
                        catch (Exception)
                        {
                            // Fallback: tentar deserializar diretamente como array
                            _logger.LogInformation("🔄 Tentando deserializar como array direto...");
                            try
                            {
                                recommendations = JsonSerializer.Deserialize<List<CostRecommendation>>(json, _jsonOptions);
                                _logger.LogInformation("✅ Deserializado como array: {count} recommendations", recommendations?.Count ?? 0);
                            }
                            catch (Exception ex2)
                            {
                                _logger.LogError(ex2, "❌ Falha em ambos os formatos para {blobName}", blob.Name);
                            }
                        }
                        
                        if (recommendations != null && recommendations.Count > 0)
                        {
                            allRecommendations.AddRange(recommendations);
                            _logger.LogInformation("📊 Total acumulado: {total} recommendations", allRecommendations.Count);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Erro ao processar blob {blobName}: {error}", blob.Name, ex.Message);
                        // Continue processando outros blobs mesmo se um falhar
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
        try
        {
            // 🎯 NOVO: Processar resultado de análise com findings
            if (analysisResult is not null)
            {
                var resultType = analysisResult.GetType();
                
                // Verificar se tem propriedade "findings" (novo formato)
                var findingsProperty = resultType.GetProperty("findings");
                if (findingsProperty != null)
                {
                    var findingsValue = findingsProperty.GetValue(analysisResult);
                    if (findingsValue is IEnumerable<object> findings)
                    {
                        var allRecommendations = new List<object>();
                        var analysisMetadata = new
                        {
                            subscription_id = resultType.GetProperty("subscription_id")?.GetValue(analysisResult),
                            analysis_date = resultType.GetProperty("analysis_date")?.GetValue(analysisResult),
                            analysis_timestamp = resultType.GetProperty("analysis_timestamp")?.GetValue(analysisResult),
                            analysis_type = resultType.GetProperty("analysis_type")?.GetValue(analysisResult)
                        };

                        // Extrair findings de cada analyzer
                        foreach (var finding in findings)
                        {
                            var findingType = finding.GetType();
                            var findingsArrayProperty = findingType.GetProperty("Findings") ?? findingType.GetProperty("findings");
                            
                            if (findingsArrayProperty != null)
                            {
                                var findingsArray = findingsArrayProperty.GetValue(finding);
                                if (findingsArray is IEnumerable<object> innerFindings)
                                {
                                    allRecommendations.AddRange(innerFindings);
                                }
                            }
                        }

                        // Criar resultado no formato esperado
                        return new
                        {
                            analysisId = Guid.NewGuid().ToString(),
                            executedAt = analysisMetadata.analysis_timestamp,
                            scope = "subscription",
                            subscriptionId = analysisMetadata.subscription_id,
                            managementGroupId = (string?)null,
                            analysisPeriodDays = 7,
                            dryRun = true,
                            recommendations = allRecommendations,
                            summary = new
                            {
                                totalResourcesAnalyzed = allRecommendations.Count,
                                totalRecommendations = allRecommendations.Count,
                                totalEstimatedMonthlySavings = allRecommendations
                                    .Where(r => r.GetType().GetProperty("estimatedMonthlySavings") != null)
                                    .Sum(r => {
                                        var prop = r.GetType().GetProperty("estimatedMonthlySavings");
                                        var value = prop?.GetValue(r);
                                        return value is decimal d ? d : 0m;
                                    })
                            }
                        };
                    }
                }
                
                // Se for FinOpsAnalysisResult (formato antigo), extrair apenas as recommendations
                if (resultType.Name == "FinOpsAnalysisResult")
                {
                    var recommendationsProperty = resultType.GetProperty("Recommendations");
                    var recommendations = recommendationsProperty?.GetValue(analysisResult);
                    return recommendations ?? new List<object>();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Erro ao extrair recommendations: {error}", ex.Message);
        }

        // Fallback: retorna original
        return analysisResult;
    }

    /// <summary>
    /// 🔄 STEPS: Salva resultado de um step específico
    /// Caminho: steps/analysisId/stepType-results.json
    /// </summary>
    public async Task SaveStepResultAsync(string analysisId, string stepType, IList<object> findings)
    {
        try
        {
            var blobPath = $"steps/{analysisId}/{stepType}-results.json";
            var blobClient = _container.GetBlobClient(blobPath);

            var stepResult = new { StepType = stepType, Findings = findings, CompletedAt = DateTime.UtcNow };
            var json = JsonSerializer.Serialize(stepResult, _jsonOptions);
            
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            await blobClient.UploadAsync(stream, overwrite: true);
            
            _logger.LogInformation("💾 [STEP] Resultado salvo: {stepType} para {analysisId}", stepType, analysisId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [STEP] Erro ao salvar step {stepType}: {error}", stepType, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// 📂 STEPS: Carrega resultado de um step específico
    /// </summary>
    public async Task<List<object>> LoadStepResultAsync(string analysisId, string stepType)
    {
        try
        {
            var blobPath = $"steps/{analysisId}/{stepType}-results.json";
            var blobClient = _container.GetBlobClient(blobPath);

            if (!await blobClient.ExistsAsync())
            {
                _logger.LogWarning("⚠️ [STEP] Resultado não encontrado: {stepType} para {analysisId}", stepType, analysisId);
                return new List<object>();
            }

            var response = await blobClient.DownloadContentAsync();
            var json = response.Value.Content.ToString();
            var stepResult = JsonSerializer.Deserialize<JsonElement>(json);
            
            // Retorna apenas os findings (case insensitive - pode ser "findings" ou "Findings")
            if (stepResult.TryGetProperty("findings", out var findingsElement) || 
                stepResult.TryGetProperty("Findings", out findingsElement))
            {
                return findingsElement.EnumerateArray().Select(f => (object)f).ToList();
            }
            
            return new List<object>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [STEP] Erro ao carregar step {stepType}: {error}", stepType, ex.Message);
            return new List<object>();
        }
    }

    /// <summary>
    /// ✅ STEPS: Marca step como concluído
    /// </summary>
    public async Task MarkStepCompletedAsync(string analysisId, string stepType)
    {
        try
        {
            var blobPath = $"steps/{analysisId}/completed-steps.json";
            var blobClient = _container.GetBlobClient(blobPath);

            var completedSteps = new List<string>();
            
            // Carrega steps já concluídos
            if (await blobClient.ExistsAsync())
            {
                var response = await blobClient.DownloadContentAsync();
                var json = response.Value.Content.ToString();
                completedSteps = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }

            // Adiciona novo step se não existe
            if (!completedSteps.Contains(stepType))
            {
                completedSteps.Add(stepType);
                var updatedJson = JsonSerializer.Serialize(completedSteps, _jsonOptions);
                
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(updatedJson));
                await blobClient.UploadAsync(stream, overwrite: true);
                
                _logger.LogInformation("✅ [STEP] Marcado como concluído: {stepType} para {analysisId}", stepType, analysisId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [STEP] Erro ao marcar step concluído {stepType}: {error}", stepType, ex.Message);
        }
    }

    /// <summary>
    /// 🔍 STEPS: Verifica quais steps já foram concluídos
    /// </summary>
    public async Task<List<string>> GetCompletedStepsAsync(string analysisId)
    {
        try
        {
            var blobPath = $"steps/{analysisId}/completed-steps.json";
            var blobClient = _container.GetBlobClient(blobPath);

            if (!await blobClient.ExistsAsync())
                return new List<string>();

            var response = await blobClient.DownloadContentAsync();
            var json = response.Value.Content.ToString();
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [STEP] Erro ao verificar steps concluídos: {error}", ex.Message);
            return new List<string>();
        }
    }
}