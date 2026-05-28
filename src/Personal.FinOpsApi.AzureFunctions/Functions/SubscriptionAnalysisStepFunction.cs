using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Application;
using System.Text;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
///  PROCESSAMENTO EM ETAPAS - Solução para timeouts do Consumption Plan
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
        
        _logger.LogInformation(" [STEP] Processando: {step} para subscription {subscription} | analysisId: {analysisId}", 
            analysisStep.Step, analysisStep.SubscriptionId, analysisStep.AnalysisId);

        try
        {
            await ExecuteStepAsync(analysisStep);
            _logger.LogInformation(" [STEP] Concluído: {step} para {subscription}", 
                analysisStep.Step, analysisStep.SubscriptionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [STEP] Erro em {step} para {subscription}: {error}", 
                analysisStep.Step, analysisStep.SubscriptionId, ex.Message);
            throw; // Rejeita mensagem para retry
        }
    }

    private async Task ExecuteStepAsync(AnalysisStepMessage step)
    {
        //  IDEMPOTÊNCIA: Verifica se step já foi executado
        if (await IsStepAlreadyCompletedAsync(step))
        {
            _logger.LogInformation("⏭ [SKIP] Step {step} já foi executado para {subscription}", 
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
            case "functionapp":
                await ExecuteFunctionAppAnalysisAsync(step);
                break;
            case "loganalytics":
                await ExecuteLogAnalyticsAnalysisAsync(step);
                break;
            case "publicip":
                await ExecutePublicIpAnalysisAsync(step);
                break;
            case "consolidate":
                await ExecuteConsolidateStepAsync(step);
                break;
            default:
                _logger.LogWarning(" [STEP] Step desconhecido: {step}", step.Step);
                break;
        }

        //  Marca step como concluído
        await MarkStepAsCompletedAsync(step);

        // Dispara consolidação assim que todos os steps obrigatórios terminarem.
        await TryTriggerConsolidationAsync(step);
    }

    /// <summary>
    ///  STEP 1: ORCHESTRATE - Envia todas as etapas para análise
    /// </summary>
    private async Task ExecuteOrchestrateStepAsync(AnalysisStepMessage step)
    {
        var analysisSteps = new[] { "storage", "vm", "appservice", "functionapp", "loganalytics", "publicip" };
        
        foreach (var nextStep in analysisSteps)
        {
            var stepMessage = new AnalysisStepMessage
            {
                AnalysisId = step.AnalysisId,
                SubscriptionId = step.SubscriptionId,
                Step = nextStep,
                CreatedAt = DateTime.UtcNow
            };

            await _queueService.SendStepMessageAsync(stepMessage);
            _logger.LogInformation(" [ORCHESTRATE] Enviado step {step} para {subscription}", 
                nextStep, step.SubscriptionId);
        }

        // Agenda a consolidação com atraso maior para dar tempo aos steps pesados (produção).
        // NOTA: TryTriggerConsolidationAsync vai disparar imediatamente quando todos os 4 steps completarem,
        // então esse agendamento é apenas um fallback/safety net.
        var consolidateMessage = new AnalysisStepMessage
        {
            AnalysisId = step.AnalysisId,
            SubscriptionId = step.SubscriptionId,
            Step = "consolidate",
            CreatedAt = DateTime.UtcNow
        };

        var consolidateAt = DateTimeOffset.UtcNow.AddMinutes(30); // Aumentado de 10 para 30 minutos
        await _queueService.ScheduleMessageAsync(
            "subscription-analysis-steps",
            JsonSerializer.Serialize(consolidateMessage),
            consolidateAt);

        _logger.LogInformation(" [ORCHESTRATE] Step consolidate agendado para {scheduledAt} (+30min) ({subscription})",
            consolidateAt, step.SubscriptionId);
    }

    /// <summary>
    ///  STEP 2-5: ANÁLISES ESPECÍFICAS - Executa apenas uma parte
    /// </summary>
    private async Task ExecuteStorageAnalysisAsync(AnalysisStepMessage step)
    {
        _logger.LogInformation(" [STORAGE] Analisando Storage Accounts para {subscription}", step.SubscriptionId);
        
        try
        {
            var findings = await _orchestrator.AnalyzeStorageAccountsOnlyAsync(step.SubscriptionId);
            await SaveStepResultAsync(step, "storage", findings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [STORAGE] Erro na análise para {subscription}. Salvando resultado vazio.", step.SubscriptionId);
            await SaveStepResultAsync(step, "storage", new List<object>());
        }
    }

    private async Task ExecuteVmAnalysisAsync(AnalysisStepMessage step)
    {
        _logger.LogInformation(" [VM] Analisando VMs para {subscription}", step.SubscriptionId);
        
        try
        {
            var findings = await _orchestrator.AnalyzeVirtualMachinesOnlyAsync(step.SubscriptionId);
            await SaveStepResultAsync(step, "vm", findings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [VM] Erro na análise para {subscription}. Salvando resultado vazio.", step.SubscriptionId);
            await SaveStepResultAsync(step, "vm", new List<object>());
        }
    }

    private async Task ExecuteAppServiceAnalysisAsync(AnalysisStepMessage step)
    {
        _logger.LogInformation(" [APPSERVICE] Analisando App Services para {subscription}", step.SubscriptionId);
        
        try
        {
            var findings = await _orchestrator.AnalyzeAppServicesOnlyAsync(step.SubscriptionId);
            await SaveStepResultAsync(step, "appservice", findings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [APPSERVICE] Erro/timeout na análise para {subscription}. Salvando resultado vazio para não bloquear consolidação.", step.SubscriptionId);
            await SaveStepResultAsync(step, "appservice", new List<object>());
        }
    }

    private async Task ExecuteFunctionAppAnalysisAsync(AnalysisStepMessage step)
    {
        _logger.LogInformation(" [FUNCTIONAPP] Analisando Function Apps para {subscription}", step.SubscriptionId);
        
        try
        {
            var findings = await _orchestrator.AnalyzeFunctionAppsOnlyAsync(step.SubscriptionId);
            await SaveStepResultAsync(step, "functionapp", findings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [FUNCTIONAPP] Erro/timeout na análise para {subscription}. Salvando resultado vazio para não bloquear consolidação.", step.SubscriptionId);
            await SaveStepResultAsync(step, "functionapp", new List<object>());
        }
    }

    private async Task ExecuteLogAnalyticsAnalysisAsync(AnalysisStepMessage step)
    {
        _logger.LogInformation(" [LOGANALYTICS] Analisando Log Analytics workspaces para {subscription}", step.SubscriptionId);
        
        try
        {
            var findings = await _orchestrator.AnalyzeLogAnalyticsOnlyAsync(step.SubscriptionId);
            await SaveStepResultAsync(step, "loganalytics", findings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [LOGANALYTICS] Erro/timeout na análise para {subscription}. Salvando resultado vazio para não bloquear consolidação.", step.SubscriptionId);
            await SaveStepResultAsync(step, "loganalytics", new List<object>());
        }
    }

    private async Task ExecutePublicIpAnalysisAsync(AnalysisStepMessage step)
    {
        _logger.LogInformation(" [PUBLIC IP] Analisando IPs Públicos para {subscription}", step.SubscriptionId);
        
        try
        {
            var findings = await _orchestrator.AnalyzePublicIpsOnlyAsync(step.SubscriptionId);
            await SaveStepResultAsync(step, "publicip", findings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [PUBLIC-IP] Erro na análise para {subscription}. Salvando resultado vazio.", step.SubscriptionId);
            await SaveStepResultAsync(step, "publicip", new List<object>());
        }
    }

    /// <summary>
    ///  STEP FINAL: CONSOLIDATE - Junta todos os resultados parciais
    ///  V2.0: Auto-reschedule se steps não completaram (sem polling bloqueante)
    /// </summary>
    private async Task ExecuteConsolidateStepAsync(AnalysisStepMessage step)
    {
        _logger.LogInformation(" [CONSOLIDATE] Iniciando consolidação para {analysisId}", step.AnalysisId);

        try
        {
            //  NOVA LÓGICA: Verificar se todos os steps estão prontos SEM polling
            var completedSteps = await _storageService.GetCompletedStepsAsync(step.AnalysisId);
            var requiredSteps = new[] { "storage", "vm", "appservice", "functionapp", "loganalytics", "publicip" };
            var missingSteps = requiredSteps.Except(completedSteps).ToList();

            if (missingSteps.Any())
            {
                //  AUTO-RESCHEDULE: Reagendar consolidate ao invés de esperar/falhar
                var retryCount = step.RetryCount ?? 0;
                var maxRetries = 6; // 6 retries × 5 min = 30 min total de espera extra
                
                if (retryCount < maxRetries)
                {
                    var nextStep = new AnalysisStepMessage
                    {
                        AnalysisId = step.AnalysisId,
                        SubscriptionId = step.SubscriptionId,
                        Step = "consolidate",
                        CreatedAt = DateTime.UtcNow,
                        RetryCount = retryCount + 1
                    };

                    var rescheduleDelay = TimeSpan.FromMinutes(5);
                    var scheduledAt = DateTimeOffset.UtcNow.Add(rescheduleDelay);
                    
                    await _queueService.ScheduleMessageAsync(
                        "subscription-analysis-steps",
                        JsonSerializer.Serialize(nextStep),
                        scheduledAt);

                    _logger.LogWarning(
                        "⏳ [CONSOLIDATE] Steps faltando: {missing}. Reagendado para +5min (tentativa {retry}/{max})",
                        string.Join(", ", missingSteps), retryCount + 1, maxRetries);
                    
                    return; // Sai sem marcar como completed
                }
                else
                {
                    _logger.LogWarning(
                        " [CONSOLIDATE] Limite de retries atingido ({max}). Consolidando com steps disponíveis: {completed}",
                        maxRetries, string.Join(", ", completedSteps));
                }
            }

            // Carrega todos os resultados parciais disponíveis
            var allFindings = await LoadAllStepResultsAsync(step);

            _logger.LogInformation(" [CONSOLIDATE] {findingsCount} findings carregados de todos os steps", allFindings.Count);

            if (allFindings.Count == 0 && missingSteps.Any())
            {
                //  Mesmo sem findings, salva resultado para indicar que a análise foi processada
                _logger.LogWarning(
                    " [CONSOLIDATE] Nenhum finding encontrado para {analysisId}. Steps completados: {completed}. Steps faltando: {missing}",
                    step.AnalysisId, string.Join(",", completedSteps), string.Join(",", missingSteps));
            }

            // Cria resultado final consolidado
            var finalResult = new
            {
                AnalysisId = step.AnalysisId,
                SubscriptionId = step.SubscriptionId,
                CompletedAt = DateTime.UtcNow,
                TotalFindings = allFindings.Count,
                Findings = allFindings,
                Recommendations = allFindings, // Alias para compatibilidade
                AnalysisType = missingSteps.Any() ? "STEP_BASED_PARTIAL" : "STEP_BASED_COMPLETE",
                CompletedSteps = completedSteps,
                MissingSteps = missingSteps
            };

            // Salva resultado final no formato esperado pela API
            var analysisDate = ResolveAnalysisDate(step.AnalysisId);
            await _storageService.SaveAsync(step.SubscriptionId, finalResult, analysisDate);

            _logger.LogInformation(" [CONSOLIDATE] Análise salva: {findings} findings para {subscription} ({type})", 
                allFindings.Count, step.SubscriptionId, missingSteps.Any() ? "PARTIAL" : "COMPLETE");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [CONSOLIDATE] Erro na consolidação para {analysisId}: {error}", 
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
                    AnalysisType = "STEP_BASED_ERROR",
                    ConsolidationError = ex.Message
                };

                var analysisDate = ResolveAnalysisDate(step.AnalysisId);
                await _storageService.SaveAsync(step.SubscriptionId, errorResult, analysisDate);
                
                _logger.LogWarning(" [CONSOLIDATE] Salvou resultado com erro: {findings} findings", 
                    partialFindings.Count);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, " [CONSOLIDATE] Falha crítica ao salvar resultado parcial: {error}", saveEx.Message);
            }
            
            throw; // Re-lança para retry
        }
    }

    /// <summary>
    ///  Verifica se step já foi executado (idempotência)
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
    ///  Marca step como concluído
    /// </summary>
    private async Task MarkStepAsCompletedAsync(AnalysisStepMessage step)
    {
        await _storageService.MarkStepCompletedAsync(step.AnalysisId, step.Step);
    }

    private async Task TryTriggerConsolidationAsync(AnalysisStepMessage step)
    {
        if (step.Step is not ("storage" or "vm" or "appservice" or "functionapp" or "loganalytics" or "publicip"))
        {
            return;
        }

        var completedSteps = await _storageService.GetCompletedStepsAsync(step.AnalysisId);
        var requiredSteps = new[] { "storage", "vm", "appservice", "functionapp", "loganalytics", "publicip" };
        var allCompleted = requiredSteps.All(required => completedSteps.Contains(required));
        var consolidateAlreadyQueuedOrDone = completedSteps.Contains("consolidate") || completedSteps.Contains("consolidate-requested");

        if (!allCompleted || consolidateAlreadyQueuedOrDone)
        {
            return;
        }

        var consolidateMessage = new AnalysisStepMessage
        {
            AnalysisId = step.AnalysisId,
            SubscriptionId = step.SubscriptionId,
            Step = "consolidate",
            CreatedAt = DateTime.UtcNow
        };

        await _storageService.MarkStepCompletedAsync(step.AnalysisId, "consolidate-requested");
        await _queueService.SendStepMessageAsync(consolidateMessage);
        _logger.LogInformation(" [STEP] Todos os steps concluídos. Consolidate enviado imediatamente para {analysisId}", step.AnalysisId);
    }

    /// <summary>
    ///  Salva resultado de um step específico
    /// </summary>
    private async Task SaveStepResultAsync(AnalysisStepMessage step, string stepType, IList<object> findings)
    {
        await _storageService.SaveStepResultAsync(step.AnalysisId, stepType, findings);
        
        _logger.LogInformation(" [STEP-SAVE] {stepType}: {count} findings salvos para {analysisId}", 
            stepType, findings.Count, step.AnalysisId);
    }

    /// <summary>
    /// ⏳ Aguarda steps anteriores terminarem (polling otimizado)
    /// </summary>
    private async Task WaitForPreviousStepsAsync(AnalysisStepMessage step, string[] requiredSteps)
    {
        var maxWaitMinutes = 20;
        var startTime = DateTime.UtcNow;
        var checkInterval = TimeSpan.FromSeconds(30);

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
                _logger.LogInformation(" [WAIT] Todos os steps anteriores concluídos para {analysisId}", step.AnalysisId);
                return;
            }

            var missingSteps = requiredSteps.Except(completedSteps);
            _logger.LogInformation("⏳ [WAIT] Aguardando steps: {missing} para {analysisId}", 
                string.Join(", ", missingSteps), step.AnalysisId);

            await Task.Delay(checkInterval);
        }

        // Timeout: continua mesmo assim mas com aviso
        _logger.LogWarning(" [WAIT] Timeout aguardando steps para {analysisId} - continuando com steps disponíveis", 
            step.AnalysisId);
    }

    /// <summary>
    ///  Carrega todos os resultados parciais dos steps
    /// </summary>
    private async Task<List<object>> LoadAllStepResultsAsync(AnalysisStepMessage step)
    {
        var allFindings = new List<object>();
        var stepTypes = new[] { "storage", "vm", "appservice", "functionapp", "loganalytics", "publicip" };

        foreach (var stepType in stepTypes)
        {
            try
            {
                var findings = await _storageService.LoadStepResultAsync(step.AnalysisId, stepType);
                allFindings.AddRange(findings);
                
                _logger.LogInformation(" [LOAD] {stepType}: {count} findings carregados", stepType, findings.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(" [LOAD] Erro carregando {stepType}: {error}", stepType, ex.Message);
            }
        }

        return allFindings;
    }

    private DateTime ResolveAnalysisDate(string analysisId)
    {
        return AnalysisStorageService.TryExtractDateFromAnalysisId(analysisId, out var parsedDate)
            ? parsedDate.Date
            : DateTime.UtcNow.Date;
    }
}

/// <summary>
///  Mensagem para processamento em etapas
/// </summary>
public class AnalysisStepMessage
{
    public string AnalysisId { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string Step { get; set; } = string.Empty; // orchestrate, storage, vm, appservice, publicip, consolidate
    public DateTime CreatedAt { get; set; }
    public int? RetryCount { get; set; } // Para controle de retries do consolidate
}
