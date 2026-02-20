using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// 📊 LOG ANALYTICS DATA COLLECTOR API: Serviço simplificado para enviar recomendações FinOps
/// 🎯 Usa Data Collector API (Opção A) - mais simples que DCR/DCE
/// 
/// Setup necessário:
/// 1. Log Analytics Workspace
/// 2. Workspace ID + Primary Shared Key
/// 3. Variáveis de ambiente (sem DCR/DCE)
/// 
/// Benefícios:
/// ✅ Setup muito mais simples
/// ✅ Funciona imediatamente 
/// ✅ Não precisa de DCR/DCE
/// ✅ Mesmas funcionalidades de dashboard/KQL
/// </summary>
public class LogAnalyticsDataCollectorService
{
    private static readonly HttpClient HttpClient = new(); // Singleton para evitar socket exhaustion
    private readonly ILogger<LogAnalyticsDataCollectorService> _logger;
    private readonly string? _workspaceId;
    private readonly string? _sharedKey;
    private readonly string _logType;
    private readonly bool _isEnabled;

    public LogAnalyticsDataCollectorService(ILogger<LogAnalyticsDataCollectorService> logger)
    {
        _logger = logger;

        // 📋 CONFIGURAÇÃO: Obtém configurações do environment (muito mais simples)
        _workspaceId = Environment.GetEnvironmentVariable("LOG_ANALYTICS_WORKSPACE_ID");
        _sharedKey = Environment.GetEnvironmentVariable("LOG_ANALYTICS_SHARED_KEY");
        _logType = Environment.GetEnvironmentVariable("LOG_ANALYTICS_LOG_TYPE") ?? "FinOpsRecommendations";
        
        _isEnabled = !string.IsNullOrEmpty(_workspaceId) && !string.IsNullOrEmpty(_sharedKey);

        if (!_isEnabled)
        {
            _logger.LogWarning("⚠️ LOG ANALYTICS DESABILITADO: WORKSPACE_ID ou SHARED_KEY não configurados");
        }
        else
        {
            _logger.LogInformation("✅ LOG ANALYTICS (Data Collector API) configurado - Workspace: {workspaceId}, LogType: {logType}", 
                _workspaceId, _logType);
        }
    }

    /// <summary>
    /// 🚀 ENVIO PRINCIPAL: Envia recomendações FinOps para Log Analytics via Data Collector API
    /// </summary>
    public async Task<bool> SendRecommendationsAsync(
        List<FinOpsLogEntry> recommendations,
        string analysisId,
        CancellationToken cancellationToken = default)
    {
        if (!_isEnabled)
        {
            _logger.LogDebug("📊 Log Analytics desabilitado - pulando envio de {count} recomendações", recommendations.Count);
            return false;
        }

        if (!recommendations.Any())
        {
            _logger.LogDebug("📊 Nenhuma recomendação para enviar ao Log Analytics");
            return true;
        }

        try
        {
            _logger.LogInformation("📊 Enviando {count} recomendações para Log Analytics via Data Collector API (análise: {analysisId})", 
                recommendations.Count, analysisId);

            // 📤 SERIALIZAR payload
            var jsonPayload = JsonSerializer.Serialize(recommendations, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false // Compacto para reduzir tamanho
            });

            // 🚀 ENVIAR via Data Collector API
            await SendToDataCollectorAsync(_logType, jsonPayload, cancellationToken);
            
            _logger.LogInformation("✅ {count} recomendações enviadas com sucesso para Log Analytics", recommendations.Count);
            
            // 📊 LOG DETALHADO: Breakdown por tipo para monitoramento
            var breakdown = recommendations
                .GroupBy(r => r.RecommendationType)
                .ToDictionary(g => g.Key, g => new { 
                    count = g.Count(), 
                    totalSavings = g.Sum(r => r.EstimatedMonthlySavings) 
                });

            foreach (var item in breakdown)
            {
                _logger.LogInformation("📈 {type}: {count} recomendações, ${savings:F2}/mês economia", 
                    item.Key, item.Value.count, item.Value.totalSavings);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao enviar recomendações para Log Analytics");
            return false;
        }
    }

    /// <summary>
    /// 🔄 CONVERTER: Transforma StandardAnalyzerResult em FinOpsLogEntry para Log Analytics
    /// </summary>
    public List<FinOpsLogEntry> ConvertToLogEntries(
        StandardAnalyzerResult analyzerResult,
        string analysisId,
        string subscriptionId,
        string analysisType,
        DateTime timestamp)
    {
        var entries = new List<FinOpsLogEntry>();

        foreach (var finding in analyzerResult.Findings)
        {
            try
            {
                var entry = new FinOpsLogEntry
                {
                    AnalysisId = analysisId,
                    Timestamp = timestamp,
                    SubscriptionId = subscriptionId,
                    ResourceId = finding.ResourceId,
                    ResourceGroupName = ExtractResourceGroupName(finding.ResourceId),
                    ResourceName = ExtractResourceName(finding.ResourceId),
                    ResourceType = finding.ResourceType,
                    RecommendationType = DetermineRecommendationType(analyzerResult.Analyzer),
                    Category = DetermineCategory(finding.ResourceType),
                    Priority = finding.Priority,
                    EstimatedMonthlySavings = finding.EstimatedMonthlySavings,
                    Action = DetermineAction(analyzerResult.Analyzer),
                    Description = finding.Description,
                    Location = finding.Location ?? "unknown",
                    ResourceTags = SerializeTags(finding.Tags),
                    AnalysisType = analysisType,
                    Metrics = SerializeMetrics(finding.Metadata),
                    ConfidenceScore = DetermineConfidenceScore(analyzerResult.Analyzer, finding),
                    CurrentMonthlyCost = finding.EstimatedMonthlyCost
                };

                entries.Add(entry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Erro ao converter finding {resourceId} para LogEntry", finding.ResourceId);
            }
        }

        return entries;
    }

    /// <summary>
    /// 📤 CORE: Envia dados via Data Collector API com autenticação HMAC
    /// </summary>
    private async Task SendToDataCollectorAsync(string logType, string jsonPayload, CancellationToken cancellationToken)
    {
        var date = DateTime.UtcNow.ToString("r");
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        // 🔐 GERAR ASSINATURA HMAC
        var signature = BuildHmacSignature(
            "POST",
            content.Headers.ContentLength!.Value,
            "application/json",
            date,
            "/api/logs");

        // 🌐 PREPARAR REQUEST
        var uri = $"https://{_workspaceId}.ods.opinsights.azure.com/api/logs?api-version=2016-04-01";
        
        HttpClient.DefaultRequestHeaders.Clear();
        HttpClient.DefaultRequestHeaders.Add("Authorization", signature);
        HttpClient.DefaultRequestHeaders.Add("Log-Type", logType);
        HttpClient.DefaultRequestHeaders.Add("x-ms-date", date);

        // 🚀 ENVIAR
        _logger.LogDebug("📤 Enviando para Data Collector API: {uri}", uri);
        var response = await HttpClient.PostAsync(uri, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Data Collector API falhou - Status: {response.StatusCode}, Error: {errorContent}");
        }
    }

    /// <summary>
    /// 🔐 HMAC: Gera assinatura HMAC SHA256 para autenticação do Data Collector API
    /// </summary>
    private string BuildHmacSignature(string method, long contentLength, string contentType, string date, string resource)
    {
        var xHeaders = "x-ms-date:" + date;
        var stringToHash = $"{method}\n{contentLength}\n{contentType}\n{xHeaders}\n{resource}";
        var bytesToHash = Encoding.UTF8.GetBytes(stringToHash);
        var keyBytes = Convert.FromBase64String(_sharedKey!);

        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(bytesToHash);
        var encodedHash = Convert.ToBase64String(hash);

        return $"SharedKey {_workspaceId}:{encodedHash}";
    }

    /// <summary>
    /// 🔍 HELPER: Extrai nome do Resource Group do resourceId
    /// </summary>
    private static string ExtractResourceGroupName(string resourceId)
    {
        try
        {
            var segments = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var rgIndex = Array.IndexOf(segments, "resourceGroups");
            return rgIndex >= 0 && rgIndex + 1 < segments.Length ? segments[rgIndex + 1] : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// 🔍 HELPER: Extrai nome do recurso do resourceId
    /// </summary>
    private static string ExtractResourceName(string resourceId)
    {
        try
        {
            return resourceId.Split('/').LastOrDefault() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// 🎯 MAPPING: Mapeia tipo do analyzer para tipo de recomendação
    /// </summary>
    private static string DetermineRecommendationType(string analyzerType)
    {
        return analyzerType.ToLowerInvariant() switch
        {
            "orphaneddiskanalyzer" => "OrphanedDisk",
            "orphanedpublicipanalyzer" => "OrphanedPublicIP", 
            "idlevmanalyzer" => "IdleVM",
            "storageanalyzer" => "UnderutilizedStorage",
            "appserviceanalyzer" => "OverprovisionedAppService",
            "duplicateresourceanalyzer" => "DuplicateResource",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// 📊 CATEGORIAS: Mapeia tipo de recurso para categoria
    /// </summary>
    private static string DetermineCategory(string resourceType)
    {
        return resourceType.ToLowerInvariant() switch
        {
            var type when type.Contains("storage") => "Storage",
            var type when type.Contains("compute") || type.Contains("virtualmachine") => "Compute",
            var type when type.Contains("network") || type.Contains("publicip") => "Network",
            var type when type.Contains("web") || type.Contains("appservice") => "AppService",
            _ => "Other"
        };
    }

    /// <summary>
    /// 🎯 AÇÕES: Mapeia analyzer para ação recomendada
    /// </summary>
    private static string DetermineAction(string analyzerType)
    {
        return analyzerType.ToLowerInvariant() switch
        {
            "orphaneddiskanalyzer" => "Delete",
            "orphanedpublicipanalyzer" => "Delete",
            "idlevmanalyzer" => "Shutdown", 
            "storageanalyzer" => "Optimize",
            "appserviceanalyzer" => "Resize",
            "duplicateresourceanalyzer" => "Consolidate",
            _ => "Review"
        };
    }

    /// <summary>
    /// 🎯 CONFIDENCE: Calcula confidence score baseado no tipo e métricas
    /// </summary>
    private static int DetermineConfidenceScore(string analyzerType, StandardFinding finding)
    {
        // Base confidence por tipo de analyzer
        var baseScore = analyzerType.ToLowerInvariant() switch
        {
            "orphaneddiskanalyzer" => 95, // Alta confiança para discos órfãos
            "orphanedpublicipanalyzer" => 90, // Alta confiança para IPs órfãos
            "idlevmanalyzer" => 75, // Média confiança, precisa validar métricas
            "storageanalyzer" => 70, // Média confiança, depende de utilização
            "appserviceanalyzer" => 65, // Média-baixa, precisa análise detalhada
            _ => 50
        };

        // Ajustar baseado na economia potencial (maior economia = maior confiança)
        if (finding.EstimatedMonthlySavings > 100) baseScore += 5;
        if (finding.EstimatedMonthlySavings > 500) baseScore += 5;

        return Math.Min(baseScore, 100);
    }

    /// <summary>
    /// 🏷️ SERIALIZAÇÃO: Converte tags para JSON string
    /// </summary>
    private static string SerializeTags(Dictionary<string, string>? tags)
    {
        if (tags == null || !tags.Any()) return "{}";

        try
        {
            return JsonSerializer.Serialize(tags);
        }
        catch
        {
            return "{}";
        }
    }

    /// <summary>
    /// 📊 SERIALIZAÇÃO: Converte métricas para JSON string
    /// </summary>
    private static string SerializeMetrics(Dictionary<string, object>? metrics)
    {
        if (metrics == null || !metrics.Any()) return "{}";

        try
        {
            return JsonSerializer.Serialize(metrics);
        }
        catch
        {
            return "{}";
        }
    }
}