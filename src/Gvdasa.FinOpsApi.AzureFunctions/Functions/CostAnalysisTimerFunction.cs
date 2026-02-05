using Gvdasa.FinOpsApi.AzureFunctions.Application;
using Gvdasa.FinOpsApi.AzureFunctions.Analyzers;
using Gvdasa.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gvdasa.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// 🎯 FUNCTION DE ANALYSIS - FREQUÊNCIAS PROFISSIONAIS FinOps
/// 
/// 🟢 DIÁRIO (3:00 AM UTC):
/// - Public IP órfãos
/// - Discos órfãos  
/// - VMs paradas
/// - Azure Advisor
/// 
/// 🟡 2X SEMANA (Terça e Sexta 3:00 AM UTC):
/// - Storage Account metrics
/// - App Service Plan metrics
/// 
/// 🚀 Estratégia: "Resource Graph first, Metrics second"
/// </summary>
public class CostAnalysisTimerFunction
{
    private readonly CostAnalysisOrchestrator _orchestrator;
    // private readonly QueueProcessingService _queueService; // DESABILITADO: Processamento direto
    private readonly ObservabilityService _observability;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostAnalysisTimerFunction> _logger;

    public CostAnalysisTimerFunction(
        CostAnalysisOrchestrator orchestrator,
        // QueueProcessingService queueService, // DESABILITADO
        ObservabilityService observability,
        IConfiguration configuration,
        ILogger<CostAnalysisTimerFunction> logger)
    {
        _orchestrator = orchestrator;
        // _queueService = queueService; // DESABILITADO
        _observability = observability;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// 🕐 ANÁLISE PRINCIPAL - Frequência baseada em ambiente
    /// 🎯 Frequência inteligente baseada no dia da semana
    /// 
    /// 🚀 PRODUÇÃO: "0 0 3 * * *" (3:00 AM UTC diariamente)
    /// 🧪 DESENVOLVIMENTO: "0 */10 * * * *" (a cada 10 minutos para testes)
    /// </summary>
    [Function("CostAnalysisTimer")]
    public async Task RunAsync(
        [TimerTrigger("%CostAnalysisSchedule%")] TimerInfo timer, // 🚀 CONFIGURADO POR VARIÁVEL DE AMBIENTE
        FunctionContext context)
    {
        var startTime = DateTime.UtcNow;
        var dayOfWeek = startTime.DayOfWeek;
        
        _logger.LogInformation("🚀 CostAnalysisTimer iniciada em {time} ({dayOfWeek})", startTime, dayOfWeek);

        try
        {
            var subscriptionIds = GetSubscriptionIds();
            _logger.LogInformation("📅 Processando {count} subscriptions via PROCESSAMENTO DIRETO (sem queue)", subscriptionIds.Count);

            // 🎯 PROCESSAMENTO DIRETO: Em vez de enfileirar, executa diretamente
            foreach (var subscriptionId in subscriptionIds)
            {
                _logger.LogInformation("🔄 Processando subscription: {subscriptionId}", subscriptionId);
                
                try
                {
                    // 🟢 ANÁLISES DIÁRIAS (executam todos os dias)
                    await ExecuteDailyAnalysisAsync(subscriptionId);

                    // 🟡 ANÁLISES 2X SEMANA (só nas terças e sextas)
                    if (dayOfWeek == DayOfWeek.Tuesday || dayOfWeek == DayOfWeek.Friday)
                    {
                        _logger.LogInformation("📅 {dayOfWeek}: Executando análises 2x semana (Storage + App Service + VM Idle)", dayOfWeek);
                        await ExecuteBiWeeklyAnalysisAsync(subscriptionId);
                    }
                    
                    _logger.LogInformation("✅ Subscription {subscriptionId} processada com sucesso", subscriptionId);
                }
                catch (Exception subEx)
                {
                    _logger.LogError(subEx, "❌ Erro ao processar subscription {subscriptionId}", subscriptionId);
                    // Continua com próxima subscription
                }
            }

            var executionTime = DateTime.UtcNow - startTime;
            _logger.LogInformation("✅ CostAnalysisTimer concluída - {count} subscriptions processadas em {duration}ms", subscriptionIds.Count, executionTime.TotalMilliseconds);
            
            // 📊 Registra métrica de sucesso
            _observability.RecordAnalyzerExecutionTime("TimerOrchestrator", executionTime, true);
        }
        catch (Exception ex)
        {
            var executionTime = DateTime.UtcNow - startTime;
            
            // 📊 Registra erro
            _observability.RecordError("CostAnalysisTimer", ex);
            _observability.RecordAnalyzerExecutionTime("TimerOrchestrator", executionTime, false);
            
            _logger.LogError(ex, "❌ Erro na execução do CostAnalysisTimer");
            throw; // Re-throw para que o Azure marque como falha
        }
    }

    /// <summary>
    /// 🟢 EXECUÇÃO DIRETA: Análises DIÁRIAS (rápidas, sem métricas pesadas)
    /// 🎯 PROCESSAMENTO DIRETO: Timer executa diretamente
    /// </summary>
    private async Task ExecuteDailyAnalysisAsync(string subscriptionId)
    {
        _logger.LogInformation("🟢 Executando análises DIÁRIAS para subscription {subscriptionId}...", subscriptionId);

        // Executa os analyzers diretamente via orchestrator
        await _orchestrator.RunDailyAnalysisAsync(subscriptionId);
        
        _logger.LogInformation("✅ Análises DIÁRIAS concluídas para {subscriptionId}", subscriptionId);
    }

    /// <summary>
    /// 🟡 EXECUÇÃO DIRETA: Análises 2X SEMANA (pesadas, com Azure Monitor)
    /// 🎯 PROCESSAMENTO DIRETO: Com circuit breaker integrado
    /// </summary>
    private async Task ExecuteBiWeeklyAnalysisAsync(string subscriptionId)
    {
        _logger.LogInformation("🟡 Executando análises 2X SEMANA para subscription {subscriptionId}...", subscriptionId);

        // Executa os analyzers pesados diretamente via orchestrator
        await _orchestrator.RunBiWeeklyAnalysisAsync(subscriptionId);
        
        _logger.LogInformation("✅ Análises 2X SEMANA concluídas para {subscriptionId}", subscriptionId);
    }

    /// <summary>
    /// 📝 Obtém lista de subscriptions (mock - em produção via Azure Resource Graph)
    /// </summary>
    private List<string> GetSubscriptionIds()
    {
        // 📝 Para demo, usando subscription padrão
        // 🚀 Em produção: Resource Graph query para listar subscriptions acessíveis
        var subscriptionId = _configuration["AZURE_SUBSCRIPTION_ID"] ?? 
                            "92dbecc2-c36d-4af2-887d-3681969e5850";
        
        return new List<string> { subscriptionId };
    }
}