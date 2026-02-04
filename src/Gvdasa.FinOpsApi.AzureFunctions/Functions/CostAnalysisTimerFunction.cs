using Gvdasa.FinOpsApi.AzureFunctions.Application;
using Gvdasa.FinOpsApi.AzureFunctions.Analyzers;
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
    private readonly UnattachedDiskAnalyzer _diskAnalyzer;
    private readonly UnusedPublicIpAnalyzer _publicIpAnalyzer;
    private readonly IdleVmAnalyzer _vmAnalyzer;
    private readonly StorageAccountAnalyzer _storageAnalyzer;
    private readonly AppServiceAnalyzer _appServiceAnalyzer;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostAnalysisTimerFunction> _logger;

    public CostAnalysisTimerFunction(
        CostAnalysisOrchestrator orchestrator,
        UnattachedDiskAnalyzer diskAnalyzer,
        UnusedPublicIpAnalyzer publicIpAnalyzer,
        IdleVmAnalyzer vmAnalyzer,
        StorageAccountAnalyzer storageAnalyzer,
        AppServiceAnalyzer appServiceAnalyzer,
        IConfiguration configuration,
        ILogger<CostAnalysisTimerFunction> logger)
    {
        _orchestrator = orchestrator;
        _diskAnalyzer = diskAnalyzer;
        _publicIpAnalyzer = publicIpAnalyzer;
        _vmAnalyzer = vmAnalyzer;
        _storageAnalyzer = storageAnalyzer;
        _appServiceAnalyzer = appServiceAnalyzer;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// 🕐 ANÁLISE PRINCIPAL - 3:00 AM UTC todo dia
    /// 🎯 Frequência inteligente baseada no dia da semana
    /// 
    /// 🚀 PRODUÇÃO: "0 0 3 * * *" (3:00 AM UTC diariamente)
    /// 🧪 DESENVOLVIMENTO: "0 */10 * * * *" (a cada 10 minutos para testes)
    /// </summary>
    [Function("CostAnalysisTimer")]
    public async Task RunAsync(
        [TimerTrigger("0 */10 * * * *")] TimerInfo timer, // 🧪 DESENVOLVIMENTO: 10 min | 🚀 PRODUÇÃO: Alterar para "0 0 3 * * *"
        FunctionContext context)
    {
        var startTime = DateTime.UtcNow;
        var dayOfWeek = startTime.DayOfWeek;
        
        _logger.LogInformation("🚀 CostAnalysisTimer iniciada em {time} ({dayOfWeek})", startTime, dayOfWeek);

        try
        {
            var subscriptionId = _configuration["AZURE_SUBSCRIPTION_ID"] ?? 
                                "92dbecc2-c36d-4af2-887d-3681969e5850";

            // 🟢 ANÁLISES DIÁRIAS (executam todos os dias)
            await RunDailyAnalysisAsync(subscriptionId);

            // 🟡 ANÁLISES 2X SEMANA (só nas terças e sextas)
            if (dayOfWeek == DayOfWeek.Tuesday || dayOfWeek == DayOfWeek.Friday)
            {
                _logger.LogInformation("📅 {dayOfWeek}: Executando análises 2x semana (Storage + App Service)", dayOfWeek);
                await RunBiWeeklyAnalysisAsync(subscriptionId);
            }
            else
            {
                _logger.LogInformation("📅 {dayOfWeek}: Pulando análises 2x semana (próxima: Terça ou Sexta)", dayOfWeek);
            }

            var executionTime = DateTime.UtcNow - startTime;
            _logger.LogInformation("✅ CostAnalysisTimer concluída em {duration}ms", executionTime.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro na execução do CostAnalysisTimer");
            throw; // Re-throw para que o Azure marque como falha
        }
    }

    /// <summary>
    /// 🟢 Executa análises DIÁRIAS (rápidas, sem métricas pesadas)
    /// </summary>
    private async Task RunDailyAnalysisAsync(string subscriptionId)
    {
        _logger.LogInformation("🟢 Iniciando análises DIÁRIAS...");

        var dailyTasks = new List<Task>
        {
            // ⚡ Public IPs órfãos (rápido - só Resource Graph)
            Task.Run(async () => 
            {
                _logger.LogInformation("📡 Analisando Public IPs órfãos...");
                var result = await _publicIpAnalyzer.AnalyzeAsync(subscriptionId, 30, false);
                _logger.LogInformation("📡 Public IPs: {findings} findings em {duration}ms", 
                    result.Findings.Count, result.ExecutionMetadata.GetValueOrDefault("executionTimeMs", 0));
            }),

            // 💿 Discos órfãos (rápido - só Resource Graph)
            Task.Run(async () => 
            {
                _logger.LogInformation("💿 Analisando Discos órfãos...");
                var result = await _diskAnalyzer.AnalyzeSubscriptionAsync(subscriptionId, 30, false);
                _logger.LogInformation("💿 Discos: {findings} findings em {duration}ms", 
                    result.Findings.Count, result.ExecutionMetadata.GetValueOrDefault("executionTimeMs", 0));
            }),

            // 🖥️ VMs paradas (rápido - só status)
            Task.Run(async () => 
            {
                _logger.LogInformation("🖥️ Analisando VMs paradas...");
                var result = await _vmAnalyzer.AnalyzeAsync(subscriptionId, 30, false);
                _logger.LogInformation("🖥️ VMs: {findings} findings em {duration}ms", 
                    result.Findings.Count, result.ExecutionMetadata.GetValueOrDefault("executionTimeMs", 0));
            })
        };

        await Task.WhenAll(dailyTasks);
        
        _logger.LogInformation("✅ Análises DIÁRIAS concluídas");
    }

    /// <summary>
    /// 🟡 Executa análises 2X SEMANA (pesadas, com métricas do Azure Monitor)
    /// </summary>
    private async Task RunBiWeeklyAnalysisAsync(string subscriptionId)
    {
        _logger.LogInformation("🟡 Iniciando análises 2X SEMANA...");

        var biWeeklyTasks = new List<Task>
        {
            // 📦 Storage Account com métricas reais (pesado - Azure Monitor)
            Task.Run(async () => 
            {
                _logger.LogInformation("📦 Analisando Storage Accounts (com métricas)...");
                var result = await _storageAnalyzer.AnalyzeSubscriptionAsync(subscriptionId, 30, false);
                var optimization = result.ExecutionMetadata.GetValueOrDefault("optimizationPercentage", 0);
                _logger.LogInformation("📦 Storage: {findings} findings, {optimization:F1}% otimização", 
                    result.Findings.Count, optimization);
            }),

            // 🌐 App Service Plans com métricas reais (pesado - Azure Monitor)
            Task.Run(async () => 
            {
                _logger.LogInformation("🌐 Analisando App Service Plans (com métricas)...");
                var result = await _appServiceAnalyzer.AnalyzeAsync(subscriptionId, 30, false);
                _logger.LogInformation("🌐 App Services: {findings} findings", result.Findings.Count);
            })
        };

        await Task.WhenAll(biWeeklyTasks);
        
        _logger.LogInformation("✅ Análises 2X SEMANA concluídas");
    }
}