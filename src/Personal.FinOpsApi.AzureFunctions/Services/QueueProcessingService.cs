using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// 🚀 QUEUE-BASED PROCESSING - Arquitetura Enterprise FinOps
/// 
/// Timer → Queue Storage (1 msg/subscription) → Function Queue Trigger → Analyzer
/// 
/// ✅ Paralelismo automático
/// ✅ Escala horizontal  
/// ✅ Muito mais barato
/// ✅ Resiliente a falhas
/// </summary>
public class QueueProcessingService
{
    private readonly QueueServiceClient _queueServiceClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<QueueProcessingService> _logger;
    
    // 📊 Queue Names
    private const string SUBSCRIPTION_ANALYSIS_QUEUE = "subscription-analysis";
    private const string STORAGE_ANALYSIS_QUEUE = "storage-analysis";
    private const string VM_ANALYSIS_QUEUE = "vm-analysis";
    private const string APPSERVICE_ANALYSIS_QUEUE = "appservice-analysis";

    public QueueProcessingService(
        QueueServiceClient queueServiceClient,
        IConfiguration configuration,
        ILogger<QueueProcessingService> logger)
    {
        _queueServiceClient = queueServiceClient;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// 🎯 Enfileira análises por subscription para processamento paralelo
    /// </summary>
    public async Task EnqueueSubscriptionAnalysisAsync(List<string> subscriptionIds, string analysisType)
    {
        var queueName = analysisType.ToLower() switch
        {
            "storage" => STORAGE_ANALYSIS_QUEUE,
            "vm" => VM_ANALYSIS_QUEUE,
            "appservice" => APPSERVICE_ANALYSIS_QUEUE,
            _ => SUBSCRIPTION_ANALYSIS_QUEUE
        };

        var queueClient = _queueServiceClient.GetQueueClient(queueName);
        await queueClient.CreateIfNotExistsAsync();

        var enqueuedCount = 0;
        foreach (var subscriptionId in subscriptionIds)
        {
            var message = new SubscriptionAnalysisMessage
            {
                SubscriptionId = subscriptionId,
                AnalysisType = analysisType,
                EnqueuedAt = DateTime.UtcNow,
                Priority = GetPriority(analysisType),
                RetryCount = 0
            };

            var jsonMessage = JsonSerializer.Serialize(message);
            await queueClient.SendMessageAsync(jsonMessage);
            enqueuedCount++;
        }

        _logger.LogInformation("🚀 Enfileiradas {count} análises de {type} para processamento paralelo", 
            enqueuedCount, analysisType);
    }

    /// <summary>
    /// 📅 Enfileira análises diárias (leves - Resource Graph only)
    /// </summary>
    public async Task EnqueueDailyAnalysisAsync(List<string> subscriptionIds)
    {
        var dailyAnalysisTypes = new[] { "publicip", "disk", "vm-powerstate" };
        
        foreach (var analysisType in dailyAnalysisTypes)
        {
            await EnqueueSubscriptionAnalysisAsync(subscriptionIds, analysisType);
        }
    }

    /// <summary>
    /// 📊 Enfileira análises 2x semana (pesadas - Azure Monitor)
    /// </summary>
    public async Task EnqueueBiWeeklyAnalysisAsync(List<string> subscriptionIds)
    {
        var biWeeklyAnalysisTypes = new[] { "storage", "vm", "appservice" };
        
        foreach (var analysisType in biWeeklyAnalysisTypes)
        {
            // ✅ Feature Flag Check
            if (IsAnalyzerEnabled(analysisType))
            {
                await EnqueueSubscriptionAnalysisAsync(subscriptionIds, analysisType);
            }
            else
            {
                _logger.LogInformation("⚪ Analyzer {type} desabilitado via feature flag", analysisType);
            }
        }
    }

    /// <summary>
    /// 🎚️ Feature Flags - Habilitar/Desabilitar analyzers via configuração
    /// </summary>
    private bool IsAnalyzerEnabled(string analysisType)
    {
        var featureFlagKey = analysisType.ToLower() switch
        {
            "storage" => "EnableStorageAnalyzer",
            "vm" => "EnableVmAnalyzer", 
            "appservice" => "EnableAppServiceAnalyzer",
            _ => "EnableDefaultAnalyzers"
        };

        return _configuration.GetValue<bool>(featureFlagKey, true); // Default: habilitado
    }

    /// <summary>
    /// 📈 Define prioridade baseada no impacto financeiro
    /// </summary>
    private int GetPriority(string analysisType)
    {
        return analysisType.ToLower() switch
        {
            "vm" => 1,          // Alto impacto financeiro
            "storage" => 2,     // Médio impacto
            "appservice" => 2,  // Médio impacto  
            _ => 3              // Baixo impacto
        };
    }
}

/// <summary>
/// 📋 Mensagem para processamento de subscription via queue
/// </summary>
public class SubscriptionAnalysisMessage
{
    public string SubscriptionId { get; set; } = "";
    public string AnalysisType { get; set; } = "";
    public DateTime EnqueuedAt { get; set; }
    public int Priority { get; set; }
    public int RetryCount { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
