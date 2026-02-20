using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Application;
using System.Text;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// 🔄 PROCESSAMENTO EM ETAPAS - Solução para timeouts do Consumption Plan
/// 
/// Quebra análises grandes em steps menores (2-5 minutos cada)
/// Cada step é uma mensagem separada no Service Bus
/// 
/// Flow:
/// 1. "orchestrator" → envia steps: storage, vm, appservice, publicip, etc
/// 2. Cada step processa apenas uma parte
/// 3. Salva resultado parcial no Blob
/// 4. Step final consolida tudo
/// </summary>
public class SubscriptionAnalysisStepFunction
{
    private readonly ILogger<SubscriptionAnalysisStepFunction> _logger;
    private readonly QueueService _queueService;
    private readonly AnalysisStorageService _storageService;
    private readonly CostAnalysisOrchestrator _orchestrator;

    public SubscriptionAnalysisStepFunction(
        ILogger<SubscriptionAnalysisStepFunction> logger,
        QueueService queueService,
        AnalysisStorageService storageService,
        CostAnalysisOrchestrator orchestrator)
    {
        _logger = logger;
        _queueService = queueService;
        _storageService = storageService;
        _orchestrator = orchestrator;
    }

    [Function("SubscriptionAnalysisStep")]
    public async Task Run([ServiceBusTrigger("subscription-analysis-steps", Connection = "ServiceBusConnection")] 
        ServiceBusReceivedMessage message)
    {
        var analysisStep = JsonSerializer.Deserialize<AnalysisStepMessage>(message.Body.ToString());
        
        _logger.LogInformation("🔄 [STEP] Processando: {step} para subscription {subscription} | analysisId: {analysisId}", 
            analysisStep.Step, analysisStep.SubscriptionId, analysisStep.AnalysisId);

        try
        {
            await ExecuteStepAsync(analysisStep);
            _logger.LogInformation("✅ [STEP] Concluído: {step} para {subscription}", 
                analysisStep.Step, analysisStep.SubscriptionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [STEP] Erro em {step} para {subscription}: {error}", 
                analysisStep.Step, analysisStep.SubscriptionId, ex.Message);
            throw; // Rejeita mensagem para retry
        }
    }

    private async Task ExecuteStepAsync(AnalysisStepMessage step)
    {
        // 🔍 IDEMPOTÊNCIA: Verifica se step já foi executado
        if (await IsStepAlreadyCompletedAsync(step))
        {
            _logger.LogInformation("⏭️ [SKIP] Step {step} já foi executado para {subscription}", 
                step.Step, step.SubscriptionId);
            return;
        }

        switch (step.Step.ToLower())
        {
            case "orchestrate":
                await ExecuteOrchestrateStepAsync(step);
                break;
            case "storage":
                await ExecuteStorageAnalysisAsync(step);
                break;
            case "vm":
                await ExecuteVmAnalysisAsync(step);
                break;
            case "appservice":
                await ExecuteAppServiceAnalysisAsync(step);
                break;
            case "publicip":
                await ExecutePublicIpAnalysisAsync(step);
                break;
            case "consolidate":
                await ExecuteConsolidateStepAsync(step);
                break;
            default:
                _logger.LogWarning("⚠️ [STEP] Step desconhecido: {step}", step.Step);
                break;
        }

        // ✅ Marca step como concluído
        await MarkStepAsCompletedAsync(step);
    }

    /// <summary>
    /// 🎯 STEP 1: ORCHESTRATE - Envia todas as etapas para análise
    /// </summary>
    private async Task ExecuteOrchestrateStepAsync(AnalysisStepMessage step)
    {
        var steps = new[] { "storage", "vm", "appservice", "publicip", "consolidate" };
        
        foreach (var nextStep in steps)
        {
            var stepMessage = new AnalysisStepMessage
            {
                AnalysisId = step.AnalysisId,
                SubscriptionId = step.SubscriptionId,
                Step = nextStep,
                CreatedAt = DateTime.UtcNow
            };

            await _queueService.SendStepMessageAsync(stepMessage);
            _logger.LogInformation("📤 [ORCHESTRATE] Enviado step {step} para {subscription}", 
                nextStep, step.SubscriptionId);
        }
    }

    /// <summary>
    /// 💾 STEP 2-5: ANÁLISES ESPECÍFICAS - Executa apenas uma parte
    /// </summary>
    private async Task ExecuteStorageAnalysisAsync(AnalysisStepMessage step)
    {
        _logger.LogInformation("💾 [STORAGE] Analisando Storage Accounts para {subscription}", step.SubscriptionId);
        
        // Roda apenas análise de Storage (método específico do orchestrator)
        var findings = await _orchestrator.AnalyzeStorageAccountsOnlyAsync(step.SubscriptionId);
        
        // Salva resultado parcial
        await SaveStepResultAsync(step, "storage", findings);
    }

    private async Task ExecuteVmAnalysisAsync(AnalysisStepMessage step)
    {
        _logger.LogInformation("🖥️ [VM] Analisando VMs para {subscription}", step.SubscriptionId);
        
        var findings = await _orchestrator.AnalyzeVirtualMachinesOnlyAsync(step.SubscriptionId);
        await SaveStepResultAsync(step, "vm", findings);
    }

    private async Task ExecuteAppServiceAnalysisAsync(AnalysisStepMessage step)
    {
        _logger.LogInformation("🌐 [APPSERVICE] Analisando App Services para {subscription}", step.SubscriptionId);
        
        var findings = await _orchestrator.AnalyzeAppServicesOnlyAsync(step.SubscriptionId);
        await SaveStepResultAsync(step, "appservice", findings);
    }

    private async Task ExecutePublicIpAnalysisAsync(AnalysisStepMessage step)
    {
        _logger.LogInformation("🌍 [PUBLIC IP] Analisando IPs Públicos para {subscription}", step.SubscriptionId);
        
        var findings = await _orchestrator.AnalyzePublicIpsOnlyAsync(step.SubscriptionId);
        await SaveStepResultAsync(step, "publicip", findings);
    }

    /// <summary>
    /// 📊 STEP FINAL: CONSOLIDATE - Junta todos os resultados parciais
    /// </summary>
    private async Task ExecuteConsolidateStepAsync(AnalysisStepMessage step)
    {
        _logger.LogInformation("📊 [CONSOLIDATE] Iniciando consolidação para {analysisId}", step.AnalysisId);

        try
        {
            // Aguarda todos os steps anteriores terminarem (com timeout menor)
            await WaitForPreviousStepsAsync(step, new[] { "storage", "vm", "appservice", "publicip" });

            // Carrega todos os resultados parciais
            var allFindings = await LoadAllStepResultsAsync(step);

            _logger.LogInformation("📊 [CONSOLIDATE] {findingsCount} findings carregados de todos os steps", allFindings.Count);

            // Cria resultado final consolidado (simplificado para evitar erros)
            var finalResult = new
            {
                AnalysisId = step.AnalysisId,
                SubscriptionId = step.SubscriptionId,
                CompletedAt = DateTime.UtcNow,
                TotalFindings = allFindings.Count,
                Findings = allFindings,
                Recommendations = allFindings, // Alias para compatibilidade
                AnalysisType = "STEP_BASED_COMPLETE"
            };

            // Salva resultado final no formato esperado pela API
            await _storageService.SaveAsync(step.SubscriptionId, finalResult, DateTime.UtcNow);

            _logger.LogInformation("✅ [CONSOLIDATE] Análise completa salva: {findings} findings para {subscription}", 
                allFindings.Count, step.SubscriptionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [CONSOLIDATE] Erro na consolidação para {analysisId}: {error}", 
                step.AnalysisId, ex.Message);
            
            // Em caso de erro, tenta salvar com o que conseguiu carregar
            try
            {
                var partialFindings = await LoadAllStepResultsAsync(step);
                var errorResult = new
                {
                    AnalysisId = step.AnalysisId,
                    SubscriptionId = step.SubscriptionId,
                    CompletedAt = DateTime.UtcNow,
                    TotalFindings = partialFindings.Count,
                    Findings = partialFindings,
                    Recommendations = partialFindings,
                    AnalysisType = "STEP_BASED_PARTIAL",
                    ConsolidationError = ex.Message
                };

                await _storageService.SaveAsync(step.SubscriptionId, errorResult, DateTime.UtcNow);
                
                _logger.LogWarning("⚠️ [CONSOLIDATE] Salvou resultado parcial após erro: {findings} findings", 
                    partialFindings.Count);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "❌ [CONSOLIDATE] Falha crítica ao salvar resultado parcial: {error}", saveEx.Message);
            }
            
            throw; // Re-lança para retry
        }
    }

    /// <summary>
    /// 🔍 Verifica se step já foi executado (idempotência)
    /// </summary>
    private async Task<bool> IsStepAlreadyCompletedAsync(AnalysisStepMessage step)
    {
        try
        {
            var completedSteps = await _storageService.GetCompletedStepsAsync(step.AnalysisId);
            return completedSteps.Contains($"{step.Step}");
        }
        catch
        {
            return false; // Se erro, assume que não foi executado
        }
    }

    /// <summary>
    /// ✅ Marca step como concluído
    /// </summary>
    private async Task MarkStepAsCompletedAsync(AnalysisStepMessage step)
    {
        await _storageService.MarkStepCompletedAsync(step.AnalysisId, step.Step);
    }

    /// <summary>
    /// 💾 Salva resultado de um step específico
    /// </summary>
    private async Task SaveStepResultAsync(AnalysisStepMessage step, string stepType, IList<object> findings)
    {
        await _storageService.SaveStepResultAsync(step.AnalysisId, stepType, findings);
        
        _logger.LogInformation("💾 [STEP-SAVE] {stepType}: {count} findings salvos para {analysisId}", 
            stepType, findings.Count, step.AnalysisId);
    }

    /// <summary>
    /// ⏳ Aguarda steps anteriores terminarem (polling otimizado)
    /// </summary>
    private async Task WaitForPreviousStepsAsync(AnalysisStepMessage step, string[] requiredSteps)
    {
        var maxWaitMinutes = 5; // Reduzido de 10 para 5 minutos
        var startTime = DateTime.UtcNow;
        var checkInterval = TimeSpan.FromSeconds(15); // Check a cada 15 segundos

        while (DateTime.UtcNow - startTime < TimeSpan.FromMinutes(maxWaitMinutes))
        {
            var completedSteps = await _storageService.GetCompletedStepsAsync(step.AnalysisId);
            var allCompleted = requiredSteps.All(s => completedSteps.Contains(s));

            _logger.LogInformation("⏳ [WAIT] Status para {analysisId}: Concluídos={completed}, Necessários={required}", 
                step.AnalysisId, 
                string.Join(",", completedSteps), 
                string.Join(",", requiredSteps));

            if (allCompleted)
            {
                _logger.LogInformation("✅ [WAIT] Todos os steps anteriores concluídos para {analysisId}", step.AnalysisId);
                return;
            }

            var missingSteps = requiredSteps.Except(completedSteps);
            _logger.LogInformation("⏳ [WAIT] Aguardando steps: {missing} para {analysisId}", 
                string.Join(", ", missingSteps), step.AnalysisId);

            await Task.Delay(checkInterval);
        }

        // Timeout: continua mesmo assim mas com aviso
        _logger.LogWarning("⚠️ [WAIT] Timeout aguardando steps para {analysisId} - continuando com steps disponíveis", 
            step.AnalysisId);
    }

    /// <summary>
    /// 📂 Carrega todos os resultados parciais dos steps
    /// </summary>
    private async Task<List<object>> LoadAllStepResultsAsync(AnalysisStepMessage step)
    {
        var allFindings = new List<object>();
        var stepTypes = new[] { "storage", "vm", "appservice", "publicip" };

        foreach (var stepType in stepTypes)
        {
            try
            {
                var findings = await _storageService.LoadStepResultAsync(step.AnalysisId, stepType);
                allFindings.AddRange(findings);
                
                _logger.LogInformation("📂 [LOAD] {stepType}: {count} findings carregados", stepType, findings.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ [LOAD] Erro carregando {stepType}: {error}", stepType, ex.Message);
            }
        }

        return allFindings;
    }
}

/// <summary>
/// 📨 Mensagem para processamento em etapas
/// </summary>
public class AnalysisStepMessage
{
    public string AnalysisId { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string Step { get; set; } = string.Empty; // orchestrate, storage, vm, appservice, publicip, consolidate
    public DateTime CreatedAt { get; set; }
}