using Personal.FinOpsApi.AzureFunctions.Application;
using Personal.FinOpsApi.AzureFunctions.Analyzers;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
///  FUNCTION DE ANALYSIS - FREQUÊNCIAS PROFISSIONAIS FinOps
/// 
///  DIÁRIO (3:00 AM UTC):
/// - Public IP órfãos
/// - Discos órfãos  
/// - VMs paradas
/// - Azure Advisor
/// 
///  2X SEMANA (Terça e Sexta 3:00 AM UTC):
/// - Storage Account metrics (VIA QUEUE se habilitado)
/// - App Service Plan metrics (VIA QUEUE se habilitado)
/// - VM Idle analysis (VIA QUEUE se habilitado)
/// 
///  HÍBRIDO: Queue processing OU execução direta baseado em feature flag
/// </summary>
public class CostAnalysisTimerFunction
{
    private readonly CostAnalysisOrchestrator _orchestrator;
    private readonly SubscriptionDiscoveryService _subscriptionDiscovery;
    private readonly QueueService _queueService; //  NOVO: Service Bus queue processing
    private readonly ObservabilityService _observability;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostAnalysisTimerFunction> _logger;

    public CostAnalysisTimerFunction(
        CostAnalysisOrchestrator orchestrator,
        SubscriptionDiscoveryService subscriptionDiscovery,
        QueueService queueService, //  NOVO: Queue service para processamento híbrido
        ObservabilityService observability,
        IConfiguration configuration,
        ILogger<CostAnalysisTimerFunction> logger)
    {
        _orchestrator = orchestrator;
        _subscriptionDiscovery = subscriptionDiscovery;
        _queueService = queueService; //  NOVO
        _observability = observability;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    ///  ANÁLISE PRINCIPAL - Frequência baseada em ambiente
    ///  Frequência inteligente baseada no dia da semana
    /// 
    ///  PRODUÇÃO: "0 0 3 * * *" (3:00 AM UTC diariamente)
    ///  DESENVOLVIMENTO: "0 */10 * * * *" (a cada 10 minutos para testes)
    /// </summary>
    [Function("CostAnalysisTimer")]
    public async Task RunAsync(
        [TimerTrigger("%CostAnalysisSchedule%")] TimerInfo timer, //  CONFIGURADO POR VARIÁVEL DE AMBIENTE
        FunctionContext context)
    {
        var startTime = DateTime.UtcNow;
        var dayOfWeek = startTime.DayOfWeek;
        
        _logger.LogInformation(" CostAnalysisTimer iniciada em {time} ({dayOfWeek})", startTime, dayOfWeek);

        try
        {
            //  DISCOVERY AUTOMÁTICO: Buscar subscriptions automaticamente
            var subscriptionIds = await _subscriptionDiscovery.DiscoverSubscriptionsAsync();
            _logger.LogInformation(" Descobertas {count} subscriptions via DISCOVERY AUTOMÁTICO (processamento direto)", subscriptionIds.Count);

            //  Log detalhado das subscriptions descobertas
            var subscriptionDetails = await _subscriptionDiscovery.GetSubscriptionDetailsAsync(subscriptionIds);
            foreach (var detail in subscriptionDetails)
            {
                _logger.LogInformation(" Subscription: {subscriptionId} - Detalhes: {details}", 
                    detail.Key, System.Text.Json.JsonSerializer.Serialize(detail.Value));
            }

            //  PROCESSAMENTO SIMPLIFICADO: Análise completa sempre
            foreach (var subscriptionId in subscriptionIds)
            {
                _logger.LogInformation(" Processando subscription: {subscriptionId}", subscriptionId);
                
                try
                {
                    //  ANÁLISE COMPLETA (todos os analyzers)
                    _logger.LogInformation(" Executando análise COMPLETA para subscription {subscriptionId}", subscriptionId);
                    
                    //  HÍBRIDO: Usar queue se habilitado, senão execução direta
                    if (_queueService.IsQueueProcessingEnabled)
                    {
                        _logger.LogInformation(" [HÍBRIDO] Enviando análise completa para SERVICE BUS QUEUES");
                        
                    //  PRODUÇÃO: Subscription específica vai para fila dedicada
                    var productionSubscriptionId = "504a622c-3995-46c5-8ba7-8edb365ed17b";
                    
                    if (subscriptionId.Equals(productionSubscriptionId, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation(" [PRODUÇÃO] Enviando subscription de produção para fila dedicada");
                        
                        //  Usar fila específica de produção
                        await _queueService.SendToProductionQueueAsync(subscriptionId, "complete");
                        _logger.LogInformation(" [PRODUÇÃO] Subscription enviada para subscription-analysis-production queue");
                    }
                    else
                    {
                        //  Outras subscriptions usam fila normal
                        var queueSent = await _queueService.SendSubscriptionAnalysisAsync(subscriptionId, "complete");
                        
                        if (queueSent)
                        {
                            _logger.LogInformation(" Subscription {subscriptionId} enviada para queue normal", subscriptionId);
                        }
                        else
                        {
                            _logger.LogWarning(" Falha ao enviar para queue - executando direto como fallback");
                            await ExecuteCompleteAnalysisAsync(subscriptionId);
                        }
                    }
                    }
                    else
                    {
                        _logger.LogInformation(" [HÍBRIDO] Executando análise completa DIRETAMENTE (queue processing desabilitado)");
                        await ExecuteCompleteAnalysisAsync(subscriptionId);
                    }
                    
                    _logger.LogInformation(" Subscription {subscriptionId} processada com sucesso", subscriptionId);
                }
                catch (Exception subEx)
                {
                    _logger.LogError(subEx, " Erro ao processar subscription {subscriptionId}", subscriptionId);
                    // Continua com próxima subscription
                }
            }

            var executionTime = DateTime.UtcNow - startTime;
            _logger.LogInformation(" CostAnalysisTimer concluída - {count} subscriptions processadas em {duration}ms", subscriptionIds.Count, executionTime.TotalMilliseconds);
            
            //  Registra métrica de sucesso
            _observability.RecordAnalyzerExecutionTime("TimerOrchestrator", executionTime, true);
        }
        catch (Exception ex)
        {
            var executionTime = DateTime.UtcNow - startTime;
            
            //  Registra erro
            _observability.RecordError("CostAnalysisTimer", ex);
            _observability.RecordAnalyzerExecutionTime("TimerOrchestrator", executionTime, false);
            
            _logger.LogError(ex, " Erro na execução do CostAnalysisTimer");
            throw; // Re-throw para que o Azure marque como falha
        }
    }

    /// <summary>
    ///  EXECUÇÃO DIRETA: Análise COMPLETA (todos os analyzers)
    ///  PROCESSAMENTO DIRETO: Timer executa análise completa diretamente
    /// </summary>
    private async Task ExecuteCompleteAnalysisAsync(string subscriptionId)
    {
        _logger.LogInformation(" Executando análise COMPLETA para subscription {subscriptionId}...", subscriptionId);

        // Executa todos os analyzers diretamente via orchestrator
        await _orchestrator.AnalyzeSubscriptionAsync(subscriptionId, "complete", false);
        
        _logger.LogInformation(" Análise COMPLETA concluída para {subscriptionId}", subscriptionId);
    }

    /// <summary>
    ///  Obtém lista de subscriptions - suporte a múltiplas subscriptions
    /// </summary>
    private List<string> GetSubscriptionIds()
    {
        //  1. Verificar variável de ambiente com múltiplas subscriptions
        var subscriptionsEnv = _configuration["AZURE_SUBSCRIPTION_IDS"];
        if (!string.IsNullOrEmpty(subscriptionsEnv))
        {
            var subscriptions = subscriptionsEnv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                 .Select(s => s.Trim())
                                                 .Where(s => !string.IsNullOrEmpty(s))
                                                 .ToList();
            
            if (subscriptions.Any())
            {
                _logger.LogInformation(" Usando {count} subscriptions da variável AZURE_SUBSCRIPTION_IDS", subscriptions.Count);
                return subscriptions;
            }
        }
        
        //  2. Fallback para subscription única
        var subscriptionId = _configuration["AZURE_SUBSCRIPTION_ID"] ?? 
                            "0ce85ffc-37b5-4729-9a86-c7db4f958628"; // Usar a subscription que está funcionando
        
        _logger.LogInformation(" Usando subscription única: {subscriptionId}", subscriptionId);
        return new List<string> { subscriptionId };
    }
}