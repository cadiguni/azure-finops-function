using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
///  FASE B - Padronização completa para Blob Storage
/// Padrão único: year=YYYY/month=MM/day=DD/XXXX/arquivo.json
/// </summary>
public class AnalysisStorageService
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<AnalysisStorageService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly bool _enableRawAnalysisStorage;

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
        _enableRawAnalysisStorage = configuration.GetValue("ENABLE_RAW_ANALYSIS_STORAGE", false);
        
        InitializeContainerAsync().Wait();
    }

    private async Task InitializeContainerAsync()
    {
        try
        {
            await _container.CreateIfNotExistsAsync(PublicAccessType.None);
            _logger.LogInformation(" Container {container} inicializado", _container.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(" Erro ao criar container: {error} - continuando...", ex.Message);
        }
    }

    /// <summary>
    ///  FASE B - Método principal padronizado
    /// Salva apenas RECOMENDAÇÕES LIMPAS em: analyses/year=YYYY/month=MM/day=DD/XXXX/recommendations.json
    /// </summary>
    public async Task SaveAsync(
        string subscriptionId, 
        object analysisResult, 
        DateTime analysisDateUtc)
    {
        try
        {
            var normalizedSubscriptionId = NormalizeSubscriptionId(subscriptionId);

            //  1. SALVAR RECOMMENDATIONS (processadas)
            var blobPath = BlobPathBuilder.BuildAnalysisPath(
                analysisDateUtc,
                normalizedSubscriptionId,
                BlobPathBuilder.FileNames.Recommendations);
            
            var blobClient = _container.GetBlobClient(blobPath);

            //  DIFERENCIAL: Extrair apenas recommendations + summary limpo
            var cleanResult = ExtractRecommendationsOnly(analysisResult);
            var newRecommendationCount = GetRecommendationCount(cleanResult);

            // Proteção: não sobrescrever arquivo existente com payload vazio.
            if (newRecommendationCount == 0 && await blobClient.ExistsAsync())
            {
                var existingCount = await GetExistingRecommendationCountAsync(blobClient);
                if (existingCount > 0)
                {
                    _logger.LogWarning(
                        " Evitando sobrescrever recommendations.json com vazio para {subscriptionId}. Existente={existingCount}, novo={newCount}",
                        normalizedSubscriptionId,
                        existingCount,
                        newRecommendationCount);
                    return;
                }
            }

            // Serializar com encoding UTF-8 e caracteres especiais
            var json = JsonSerializer.Serialize(cleanResult, _jsonOptions);
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

            await blobClient.UploadAsync(stream, overwrite: true);
            _logger.LogInformation(" Recomendações limpas salvas: {path}", blobPath);

            //  2. SALVAR RAW DATA (para debug/auditoria)
            if (_enableRawAnalysisStorage)
            {
                await SaveRawAsync(normalizedSubscriptionId, analysisResult, analysisDateUtc);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(" Erro ao salvar no Storage: {error} - Salvaria: {subscription}", 
                ex.Message, subscriptionId);
        }
    }

    /// <summary>
    ///  DADOS RAW - Salva dados brutos para debug/auditoria
    /// Caminho: raw-analysis/year=YYYY/month=MM/day=DD/XXXX/raw-findings.json
    /// </summary>
    public async Task SaveRawAsync(
        string subscriptionId, 
        object analysisResult, 
        DateTime analysisDateUtc)
    {
        try
        {
            var normalizedSubscriptionId = NormalizeSubscriptionId(subscriptionId);
            var rawBlobPath = $"raw-analysis/year={analysisDateUtc:yyyy}/month={analysisDateUtc:MM}/day={analysisDateUtc:dd}/{normalizedSubscriptionId}/raw-findings.json";
            var rawBlobClient = _container.GetBlobClient(rawBlobPath);

            // Salvar dados brutos completos (sem processamento)
            var rawJson = JsonSerializer.Serialize(analysisResult, _jsonOptions);
            using var rawStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawJson));

            await rawBlobClient.UploadAsync(rawStream, overwrite: true);
            _logger.LogInformation(" Dados RAW salvos: {path}", rawBlobPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(" Erro ao salvar RAW: {error}", ex.Message);
        }
    }

    private string NormalizeSubscriptionId(string subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return "unknown-subscription";
        }

        var trimmed = subscriptionId.Trim();
        if (Guid.TryParse(trimmed, out var parsedGuid))
        {
            return parsedGuid.ToString();
        }

        // Defesa contra payload JSON usado como "id".
        if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("SubscriptionId", out var subIdElement))
                {
                    var extracted = subIdElement.GetString();
                    if (!string.IsNullOrWhiteSpace(extracted) && Guid.TryParse(extracted, out var extractedGuid))
                    {
                        _logger.LogWarning(" SubscriptionId inválido recebido no save. Extraído SubscriptionId do payload JSON.");
                        return extractedGuid.ToString();
                    }
                }
            }
            catch
            {
                // ignora e cai no fallback abaixo
            }
        }

        _logger.LogWarning(" SubscriptionId em formato inesperado: {subscriptionId}. Usando valor sanitizado.", trimmed);
        var safe = new string(trimmed.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "unknown-subscription" : safe[..Math.Min(64, safe.Length)];
    }

    /// <summary>
    ///  Lista todas as subscriptions analisadas em uma data específica
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
            _logger.LogError(" Erro ao listar subscriptions: {error}", ex.Message);
            return new List<string>();
        }
    }

    /// <summary>
    ///  Carrega análise específica de uma subscription em uma data
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
                _logger.LogWarning(" Blob não encontrado: {path}", blobPath);
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
            _logger.LogError(" Erro ao carregar análise: {error}", ex.Message);
            return new List<CostRecommendation>();
        }
    }

    /// <summary>
    ///  Obtém o cliente do container para debug (uso interno)
    /// </summary>
    public BlobContainerClient GetContainerClient()
    {
        return _container;
    }

    /// <summary>
    ///  Carrega todas as análises de um dia específico
    /// </summary>
    public async Task<List<CostRecommendation>> GetDailyAnalysisAsync(DateTime date)
    {
        try
        {
            var prefix = BlobPathBuilder.BuildAnalysesDailyPrefix(date);
            var allRecommendations = new List<CostRecommendation>();
            
            _logger.LogInformation(" Buscando análises com prefixo: {prefix}", prefix);

            await foreach (var blob in _container.GetBlobsAsync(prefix: prefix))
            {
                // Apenas arquivos de recomendações, não raw-analysis
                if (blob.Name.EndsWith(BlobPathBuilder.FileNames.Recommendations))
                {
                    try
                    {
                        _logger.LogInformation(" Processando blob: {blobName}", blob.Name);
                        
                        var blobClient = _container.GetBlobClient(blob.Name);
                        
                        // Verificar se o blob existe e tem conteúdo
                        var properties = await blobClient.GetPropertiesAsync();
                        _logger.LogInformation(" Tamanho do blob: {size} bytes", properties.Value.ContentLength);
                        
                        if (properties.Value.ContentLength == 0)
                        {
                            _logger.LogWarning(" Blob vazio: {blobName}", blob.Name);
                            continue;
                        }
                        
                        var response = await blobClient.DownloadStreamingAsync();
                        using var reader = new StreamReader(response.Value.Content);
                        var json = await reader.ReadToEndAsync();
                        
                        _logger.LogInformation(" JSON length: {length}, starts with: {start}", 
                            json.Length, 
                            json.Length > 50 ? json.Substring(0, 50) : json);
                        
                        if (string.IsNullOrWhiteSpace(json))
                        {
                            _logger.LogWarning(" JSON vazio ou null: {blobName}", blob.Name);
                            continue;
                        }
                        
                        //  NOVA LÓGICA: Primeiro tentar como AnalysisResult completo
                        List<CostRecommendation> recommendations = null;
                        
                        try
                        {
                            var analysisResult = JsonSerializer.Deserialize<FullAnalysisResult>(json, _jsonOptions);
                            if (analysisResult?.Recommendations != null)
                            {
                                recommendations = analysisResult.Recommendations;
                                _logger.LogInformation(" Deserializado como FullAnalysisResult: {count} recommendations", recommendations.Count);
                            }
                        }
                        catch (Exception)
                        {
                            // Fallback: tentar deserializar diretamente como array
                            _logger.LogInformation(" Tentando deserializar como array direto...");
                            try
                            {
                                recommendations = JsonSerializer.Deserialize<List<CostRecommendation>>(json, _jsonOptions);
                                _logger.LogInformation(" Deserializado como array: {count} recommendations", recommendations?.Count ?? 0);
                            }
                            catch (Exception ex2)
                            {
                                _logger.LogError(ex2, " Falha em ambos os formatos para {blobName}", blob.Name);
                            }
                        }
                        
                        if (recommendations != null && recommendations.Count > 0)
                        {
                            allRecommendations.AddRange(recommendations);
                            _logger.LogInformation(" Total acumulado: {total} recommendations", allRecommendations.Count);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, " Erro ao processar blob {blobName}: {error}", blob.Name, ex.Message);
                        // Continue processando outros blobs mesmo se um falhar
                    }
                }
            }
            
            // 🔄 DEDUPLICAÇÃO: Mantém a recomendação com DailyCost > 0 ou a mais recente
            // Isso evita que análises antigas sobrescrevam dados mais recentes
            var deduplicated = allRecommendations
                .GroupBy(r => r.ResourceId)
                .Select(g => g.OrderByDescending(r => r.DailyCost > 0 ? 1 : 0)
                              .ThenByDescending(r => r.EstimatedMonthlyCost)
                              .First())
                .ToList();
            
            _logger.LogInformation(" Carregadas {count} cost findings (após deduplicação de {original})", 
                deduplicated.Count, allRecommendations.Count);
            return deduplicated;
        }
        catch (Exception ex)
        {
            _logger.LogError(" Erro ao carregar análises diárias: {error}", ex.Message);
            return new List<CostRecommendation>();
        }
    }

    /// <summary>
    ///  Extrai APENAS a lista de recomendações para recommendations.json
    /// Remove todos os metadados, deixa só as ações concretas
    /// </summary>
    private object ExtractRecommendationsOnly(object analysisResult)
    {
        try
        {
            if (analysisResult is not null)
            {
                var resultType = analysisResult.GetType();

                // 1) Formato com Recommendations/recommendations direto.
                var directRecommendationsProperty =
                    resultType.GetProperty("Recommendations") ??
                    resultType.GetProperty("recommendations");

                if (directRecommendationsProperty != null)
                {
                    var directRecommendations = directRecommendationsProperty.GetValue(analysisResult) as System.Collections.IEnumerable;
                    if (directRecommendations != null)
                    {
                        var recommendationsList = directRecommendations.Cast<object>().ToList();
                        return BuildRecommendationsEnvelope(resultType, analysisResult, recommendationsList);
                    }
                }

                // 2) Formato com Findings/findings (lista de analyzer results).
                var findingsProperty = resultType.GetProperty("Findings") ?? resultType.GetProperty("findings");
                if (findingsProperty != null)
                {
                    var findingsValue = findingsProperty.GetValue(analysisResult);
                    if (findingsValue is IEnumerable<object> findings)
                    {
                        var allRecommendations = new List<object>();

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
                                continue;
                            }

                            // Se já for item de recomendação/finding, adiciona direto.
                            allRecommendations.Add(finding);
                        }

                        return BuildRecommendationsEnvelope(resultType, analysisResult, allRecommendations);
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
            _logger.LogWarning(" Erro ao extrair recommendations: {error}", ex.Message);
        }

        // Fallback seguro: nunca salvar payload bruto em recommendations.json.
        return new
        {
            analysisId = Guid.NewGuid().ToString(),
            executedAt = DateTime.UtcNow,
            scope = "subscription",
            subscriptionId = string.Empty,
            managementGroupId = (string?)null,
            analysisPeriodDays = 7,
            dryRun = true,
            recommendations = Array.Empty<object>(),
            summary = new
            {
                totalResourcesAnalyzed = 0,
                totalRecommendations = 0,
                totalEstimatedMonthlySavings = 0m
            }
        };
    }

    private object BuildRecommendationsEnvelope(Type resultType, object analysisResult, List<object> allRecommendations)
    {
        var analysisId =
            resultType.GetProperty("AnalysisId")?.GetValue(analysisResult)?.ToString() ??
            resultType.GetProperty("analysisId")?.GetValue(analysisResult)?.ToString() ??
            Guid.NewGuid().ToString();

        var subscriptionId =
            resultType.GetProperty("SubscriptionId")?.GetValue(analysisResult)?.ToString() ??
            resultType.GetProperty("subscription_id")?.GetValue(analysisResult)?.ToString() ??
            resultType.GetProperty("subscriptionId")?.GetValue(analysisResult)?.ToString() ??
            string.Empty;

        var executedAt =
            resultType.GetProperty("ExecutedAt")?.GetValue(analysisResult) ??
            resultType.GetProperty("analysis_timestamp")?.GetValue(analysisResult) ??
            DateTime.UtcNow;

        decimal totalEstimatedMonthlySavings = 0m;
        foreach (var recommendation in allRecommendations)
        {
            try
            {
                var recommendationType = recommendation.GetType();
                var savingsProp =
                    recommendationType.GetProperty("estimatedMonthlySavings") ??
                    recommendationType.GetProperty("EstimatedMonthlySavings") ??
                    recommendationType.GetProperty("PotentialMonthlySavings");

                if (savingsProp == null)
                {
                    continue;
                }

                var value = savingsProp.GetValue(recommendation);
                if (value is decimal d) totalEstimatedMonthlySavings += d;
                else if (value is double db) totalEstimatedMonthlySavings += (decimal)db;
                else if (value is float f) totalEstimatedMonthlySavings += (decimal)f;
                else if (value is int i) totalEstimatedMonthlySavings += i;
                else if (value is long l) totalEstimatedMonthlySavings += l;
                else if (value is string s && decimal.TryParse(s, out var parsed)) totalEstimatedMonthlySavings += parsed;
            }
            catch
            {
                // Ignora item inválido de forma isolada.
            }
        }

        return new
        {
            analysisId = analysisId,
            executedAt = executedAt,
            scope = "subscription",
            subscriptionId = subscriptionId,
            managementGroupId = (string?)null,
            analysisPeriodDays = 7,
            dryRun = true,
            recommendations = allRecommendations,
            summary = new
            {
                totalResourcesAnalyzed = allRecommendations.Count,
                totalRecommendations = allRecommendations.Count,
                totalEstimatedMonthlySavings = totalEstimatedMonthlySavings
            }
        };
    }

    private static int GetRecommendationCount(object cleanResult)
    {
        if (cleanResult is IEnumerable<object> enumerable)
        {
            return enumerable.Count();
        }

        var resultType = cleanResult.GetType();
        var recommendationsProperty =
            resultType.GetProperty("recommendations") ??
            resultType.GetProperty("Recommendations");

        if (recommendationsProperty == null)
        {
            return 0;
        }

        var value = recommendationsProperty.GetValue(cleanResult);
        if (value is System.Collections.IEnumerable items)
        {
            var count = 0;
            foreach (var _ in items)
            {
                count++;
            }
            return count;
        }

        return 0;
    }

    private async Task<int> GetExistingRecommendationCountAsync(BlobClient blobClient)
    {
        try
        {
            var response = await blobClient.DownloadContentAsync();
            var json = response.Value.Content.ToString();
            if (string.IsNullOrWhiteSpace(json))
            {
                return 0;
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement.GetArrayLength();
            }

            if (doc.RootElement.TryGetProperty("recommendations", out var recommendationsElement) &&
                recommendationsElement.ValueKind == JsonValueKind.Array)
            {
                return recommendationsElement.GetArrayLength();
            }

            if (doc.RootElement.TryGetProperty("Recommendations", out var recommendationsPascalElement) &&
                recommendationsPascalElement.ValueKind == JsonValueKind.Array)
            {
                return recommendationsPascalElement.GetArrayLength();
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(" Erro ao ler recommendations existente para proteção de overwrite: {error}", ex.Message);
            return 0;
        }
    }

    /// <summary>
    ///  STEPS: Salva resultado de um step específico
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
            
            _logger.LogInformation(" [STEP] Resultado salvo: {stepType} para {analysisId}", stepType, analysisId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [STEP] Erro ao salvar step {stepType}: {error}", stepType, ex.Message);
            throw;
        }
    }

    /// <summary>
    ///  STEPS: Carrega resultado de um step específico
    /// </summary>
    public async Task<List<object>> LoadStepResultAsync(string analysisId, string stepType)
    {
        try
        {
            var blobPath = $"steps/{analysisId}/{stepType}-results.json";
            var blobClient = _container.GetBlobClient(blobPath);

            if (!await blobClient.ExistsAsync())
            {
                _logger.LogWarning(" [STEP] Resultado não encontrado: {stepType} para {analysisId}", stepType, analysisId);
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
            _logger.LogError(ex, " [STEP] Erro ao carregar step {stepType}: {error}", stepType, ex.Message);
            return new List<object>();
        }
    }

    /// <summary>
    ///  STEPS: Marca step como concluído
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
                
                _logger.LogInformation(" [STEP] Marcado como concluído: {stepType} para {analysisId}", stepType, analysisId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [STEP] Erro ao marcar step concluído {stepType}: {error}", stepType, ex.Message);
        }
    }

    /// <summary>
    ///  STEPS: Verifica quais steps já foram concluídos
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
            _logger.LogError(ex, " [STEP] Erro ao verificar steps concluídos: {error}", ex.Message);
            return new List<string>();
        }
    }

    public static bool TryExtractDateFromAnalysisId(string analysisId, out DateTime date)
    {
        date = default;

        if (string.IsNullOrWhiteSpace(analysisId))
        {
            return false;
        }

        var parts = analysisId.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            return false;
        }

        var dateText = string.Join('-', parts.Skip(parts.Length - 3));
        return DateTime.TryParseExact(
            dateText,
            "yyyy-MM-dd",
            null,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out date);
    }

    /// <summary>
    ///  Verifica se existe arquivo recommendations.json para subscription/data
    /// </summary>
    public async Task<bool> HasRecommendationsAsync(string subscriptionId, DateTime date)
    {
        try
        {
            var normalizedSubscriptionId = NormalizeSubscriptionId(subscriptionId);
            var blobPath = BlobPathBuilder.BuildAnalysisPath(date, normalizedSubscriptionId, "recommendations.json");
            var blobClient = _container.GetBlobClient(blobPath);
            
            return await blobClient.ExistsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(" [HAS-RECOMMENDATIONS] Erro ao verificar: {error}", ex.Message);
            return false;
        }
    }
}
