using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Application;
using Personal.FinOpsApi.AzureFunctions.Analyzers;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Functions;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class CostAnalysisFunctions
{
    private readonly CostAnalysisOrchestrator _orchestrator;
    private readonly FinOpsResultAggregator _resultAggregator;
    private readonly AnalysisStorageService _storageService;
    private readonly QueueService _queueService;
    private readonly ILogger<CostAnalysisFunctions> _logger;

    public CostAnalysisFunctions(
        ILogger<CostAnalysisFunctions> logger, 
        CostAnalysisOrchestrator orchestrator, 
        FinOpsResultAggregator resultAggregator,
        AnalysisStorageService storageService,
        QueueService queueService)
    {
        _logger = logger;
        _orchestrator = orchestrator;
        _resultAggregator = resultAggregator;
        _storageService = storageService;
        _queueService = queueService;
    }

    [Function("health")]
    public HttpResponseData Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.WriteString(" FinOps Cost Analysis API");
        return response;
    }

    [Function("analyze-costs")]
    public async Task<HttpResponseData> AnalyzeCosts(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
    {
        try
        {
            // Parse request
            CostAnalysisRequest request;
            
            if (req.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                _logger.LogInformation($"Request body received: {requestBody}");
                
                if (string.IsNullOrWhiteSpace(requestBody))
                {
                    request = new CostAnalysisRequest();
                }
                else
                {
                    try
                    {
                        request = JsonSerializer.Deserialize<CostAnalysisRequest>(requestBody, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                        }) ?? new CostAnalysisRequest();
                        
                        _logger.LogInformation($" POST Parsed - SubscriptionId: {request.SubscriptionId}, DryRun: {request.DryRun}, Scope: {request.Scope}");
                        _logger.LogInformation(" POST Options: {options}", JsonSerializer.Serialize(request.AnalysisOptions));
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError($"JSON deserialization error: {ex.Message}");
                        request = new CostAnalysisRequest();
                    }
                }
            }
            else
            {
                //  DEBUG: Log detalhado do GET request
                _logger.LogInformation(" GET Request - iniciando parsing...");
                
                // GET com múltiplas análises habilitadas
                var subscriptionId = req.Query["subscriptionId"] ?? "";
                
                //  Corrigir parsing do dryRun da query string - PROTEÇÃO ANTI-NULL
                var dryRunQuery = req.Query["dryRun"] ?? "";
                
                bool dryRun = !string.IsNullOrEmpty(dryRunQuery) && bool.TryParse(dryRunQuery, out var parsedDryRun) ? parsedDryRun : true; //  DEFAULT SAFE
                
                _logger.LogInformation("GET request - subscriptionId: {subscriptionId}, dryRun: {dryRun}", subscriptionId, dryRun);
                
                _logger.LogInformation(" Criando CostAnalysisRequest...");
                request = new CostAnalysisRequest
                {
                    Scope = "subscription",
                    SubscriptionId = string.IsNullOrEmpty(subscriptionId) ? null : subscriptionId,
                    DryRun = dryRun,
                    AnalysisOptions = new AnalysisIncludeOptions 
                    { 
                        UnattachedDisks = true,
                        StorageAccounts = true,
                        UnusedPublicIps = true
                    }
                };
                //  BLINDAGEM: Garantir que AnalysisOptions nunca seja null
                request.AnalysisOptions ??= new AnalysisIncludeOptions 
                { 
                    UnattachedDisks = true,
                    StorageAccounts = true,
                    UnusedPublicIps = true
                };
                
                //  REGRA FinOps: GET sem dryRun explícito = sempre dry-run (seguro)
                if (string.IsNullOrEmpty(req.Query["dryRun"]) && req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    request.DryRun = true;
                    _logger.LogInformation(" GET sem dryRun explícito → forçando dryRun=true (seguro)");
                }
            }

            // Usar orchestrator injetado via DI
            _logger.LogInformation("Iniciando análise com múltiplos analyzers em paralelo...");
            
            //  DEBUG: Verificar se orchestrator está injetado
            _logger.LogInformation(" Orchestrator: {orch}", _orchestrator == null ? "NULL" : "NOT NULL");
            
            //  PROTEÇÃO: Modo real ainda não implementado
            if (!request.DryRun)
            {
                _logger.LogWarning(" EXECUTANDO ANÁLISE EM MODO REAL (dryRun=false) para subscription {sub}", request.SubscriptionId);
                
                // Por enquanto, só analisamos - não executamos ações reais
                _logger.LogInformation(" Executando análise em modo somente-leitura (análise + recomendações)");
            }
            
            _logger.LogInformation(" Antes de chamar ExecuteAnalysisAsync");
            var result = await _orchestrator.ExecuteAnalysisAsync(request);
            _logger.LogInformation(" Depois de ExecuteAnalysisAsync - result: {res}", result == null ? "NULL" : "NOT NULL");

            //  OPÇÃO B: Salvar no Storage estruturado (data → subscription)
            try
            {
                var finOpsResult = new FinOpsAnalysisResult
                {
                    AnalysisId = Guid.Parse(result.AnalysisId),
                    ExecutedAt = result.ExecutedAt,
                    SubscriptionId = result.SubscriptionId ?? "",
                    ManagementGroupId = result.ManagementGroupId,
                    DryRun = result.DryRun,
                    AnalysisPeriodDays = result.AnalysisPeriodDays,
                    Recommendations = result.Recommendations,
                    Summary = FinOpsResultAggregator.BuildSummary(result.Recommendations)
                };

                // Salvar usando estrutura OPÇÃO B
                await _storageService.SaveAsync(
                    subscriptionId: finOpsResult.SubscriptionId,
                    analysisResult: finOpsResult,
                    analysisDateUtc: finOpsResult.ExecutedAt);

                // Manter compatibilidade com o agregador antigo
                await _resultAggregator.SaveAnalysisResultAsync(finOpsResult);
                
                _logger.LogInformation(" Resultado salvo em ambos os formatos (OPÇÃO B + legacy)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, " Erro ao salvar resultado histórico - continuando execução");
            }

            //  Log de contexto sobre resultados em modo real
            if (!request.DryRun && result.Recommendations.Any())
            {
                _logger.LogInformation(" Modo real: {count} recomendações encontradas. Executor de ações ainda não implementado.", result.Recommendations.Count);
            }

            // Resposta
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            
            await response.WriteStringAsync(json);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " ERRO CAPTURADO - Stack trace completo:");
            
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            errorResponse.Headers.Add("Content-Type", "application/json");
            
            await errorResponse.WriteAsJsonAsync(new { error = "Erro interno ao executar análise de custos." });
            
            return errorResponse;
        }
    }

    // Manter função legada para compatibilidade
    [Function("collect-costs")]
    public async Task<HttpResponseData> CollectCosts(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
    {
        try
        {
            // Criar configuração padrão
            var request = new CostAnalysisRequest
            {
                Scope = "subscription",
                SubscriptionId = req.Query["subscriptionId"],
                AnalysisOptions = new AnalysisIncludeOptions { UnattachedDisks = true }
            };

            // Executar análise usando orchestrator injetado via DI
            var result = await _orchestrator.ExecuteAnalysisAsync(request);

            // Resposta
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            
            await response.WriteStringAsync(json);
            return response;
        }
        catch (Exception ex)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            errorResponse.Headers.Add("Content-Type", "application/json");
            
            var errorResult = new
            {
                error = true,
                message = "Erro interno ao coletar custos.",
                timestamp = DateTime.UtcNow
            };
            
            var errorJson = JsonSerializer.Serialize(errorResult, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            
            await errorResponse.WriteStringAsync(errorJson);
            return errorResponse;
        }
    }

    /// <summary>
    ///  ANÁLISE STATUS - Verifica status dos steps de uma análise
    /// Útil para diagnosticar por que produção não está completando
    /// </summary>
    [Function("analysis-status")]
    public async Task<HttpResponseData> AnalysisStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var subscriptionId = query["subscriptionId"];
            var date = query["date"] ?? DateTime.UtcNow.ToString("yyyy-MM-dd");

            if (string.IsNullOrEmpty(subscriptionId))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteAsJsonAsync(new { error = "Parâmetro subscriptionId é obrigatório" });
                return badResponse;
            }

            var analysisId = $"{subscriptionId}-{date}";
            
            _logger.LogInformation(" [ANALYSIS-STATUS] Verificando status para {analysisId}", analysisId);

            // Verificar steps concluídos
            var completedSteps = await _storageService.GetCompletedStepsAsync(analysisId);
            var requiredSteps = new[] { "storage", "vm", "appservice", "functionapp", "loganalytics", "publicip" };
            var missingSteps = requiredSteps.Except(completedSteps).ToList();
            var consolidateCompleted = completedSteps.Contains("consolidate");
            var allStepsCompleted = requiredSteps.All(s => completedSteps.Contains(s));

            // Verificar se há dados parciais
            var stepDetails = new Dictionary<string, object>();
            foreach (var step in requiredSteps)
            {
                try
                {
                    var findings = await _storageService.LoadStepResultAsync(analysisId, step);
                    stepDetails[step] = new
                    {
                        completed = completedSteps.Contains(step),
                        findingsCount = findings.Count,
                        hasData = findings.Count > 0
                    };
                }
                catch
                {
                    stepDetails[step] = new
                    {
                        completed = completedSteps.Contains(step),
                        findingsCount = 0,
                        hasData = false,
                        error = "Não foi possível carregar dados do step"
                    };
                }
            }

            // Verificar se há resultado final (recommendations.json)
            var hasRecommendations = await _storageService.HasRecommendationsAsync(subscriptionId, DateTime.Parse(date));

            var status = new
            {
                analysisId,
                subscriptionId,
                date,
                timestamp = DateTime.UtcNow,
                
                // Status geral
                status = consolidateCompleted ? "COMPLETED" 
                    : allStepsCompleted ? "READY_TO_CONSOLIDATE" 
                    : "IN_PROGRESS",
                
                // Steps
                completedSteps = completedSteps,
                missingSteps = missingSteps,
                allStepsCompleted,
                consolidateCompleted,
                
                // Detalhes por step
                steps = stepDetails,
                
                // Resultado final
                hasRecommendationsFile = hasRecommendations,
                
                // Ações sugeridas
                suggestedAction = !allStepsCompleted 
                    ? $"Aguarde steps completarem: {string.Join(", ", missingSteps)}"
                    : !consolidateCompleted 
                        ? "Rode force-consolidate manualmente ou aguarde retry automático"
                        : hasRecommendations
                            ? "Análise completa - dados disponíveis no relatório"
                            : "Consolidado mas sem recommendations.json - verificar logs"
            };

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(status);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [ANALYSIS-STATUS] Erro: {error}", ex.Message);
            
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Erro interno ao verificar status da análise." });
            return errorResponse;
        }
    }

    /// <summary>
    ///  TRIGGER ANÁLISE COM STEPS - Dispara análise manualmente usando sistema de steps
    /// Útil para testar análise de produção sem esperar o timer das 3:00 AM
    /// </summary>
    [Function("trigger-analysis")]
    public async Task<HttpResponseData> TriggerAnalysis(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get")] HttpRequestData req)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var subscriptionId = query["subscriptionId"];
            var useSteps = query["useSteps"]?.ToLower() != "false"; // Default: true

            if (string.IsNullOrEmpty(subscriptionId))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteAsJsonAsync(new { 
                    error = "Parâmetro subscriptionId é obrigatório",
                    usage = "/api/trigger-analysis?subscriptionId=xxx&useSteps=true"
                });
                return badResponse;
            }

            _logger.LogInformation(" [TRIGGER-ANALYSIS] Disparando análise para {subscriptionId} (useSteps={useSteps})", 
                subscriptionId, useSteps);

            var analysisId = $"{subscriptionId}-{DateTime.UtcNow:yyyy-MM-dd}";

            if (useSteps)
            {
                // Enviar diretamente para o sistema de steps (bypassa filas intermediárias)
                var orchestrateMessage = new AnalysisStepMessage
                {
                    AnalysisId = analysisId,
                    SubscriptionId = subscriptionId,
                    Step = "orchestrate",
                    CreatedAt = DateTime.UtcNow
                };

                await _queueService.SendStepMessageAsync(orchestrateMessage);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = "Análise com steps iniciada",
                    analysisId,
                    subscriptionId,
                    triggeredAt = DateTime.UtcNow,
                    expectedFlow = new[]
                    {
                        "1. orchestrate → envia 6 steps",
                        "2. storage, vm, appservice, functionapp, loganalytics, publicip → rodam em paralelo",
                        "3. consolidate → junta resultados (automático ou +30min)",
                        "4. recommendations.json → salvo no blob"
                    },
                    monitorWith = $"/api/analysis-status?subscriptionId={subscriptionId}&date={DateTime.UtcNow:yyyy-MM-dd}"
                });
                return response;
            }
            else
            {
                // Execução direta (pode dar timeout em subscriptions grandes!)
                _logger.LogWarning(" [TRIGGER-ANALYSIS] Executando análise DIRETA - pode dar timeout em subscriptions grandes!");
                
                var result = await _orchestrator.ExecuteAnalysisAsync(new CostAnalysisRequest
                {
                    Scope = "subscription",
                    SubscriptionId = subscriptionId,
                    DryRun = false,
                    AnalysisOptions = new AnalysisIncludeOptions
                    {
                        UnattachedDisks = true,
                        StorageAccounts = true,
                        UnusedPublicIps = true,
                        IdleVms = true,
                        AppServices = true
                    }
                });

                // Salvar resultado
                await _storageService.SaveAsync(subscriptionId, result, DateTime.UtcNow.Date);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = "Análise direta executada",
                    subscriptionId,
                    recommendationsCount = result.Recommendations?.Count ?? 0,
                    totalSavings = result.Summary?.TotalEstimatedMonthlySavings ?? 0
                });
                return response;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [TRIGGER-ANALYSIS] Erro: {error}", ex.Message);
            
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Erro interno ao disparar a análise." });
            return errorResponse;
        }
    }

    /// <summary>
    ///  FORÇAR CONSOLIDAÇÃO - Para testar consolidação manual de steps
    /// </summary>
    [Function("force-consolidate")]
    public async Task<HttpResponseData> ForceConsolidate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var subscriptionId = query["subscriptionId"];
            var date = query["date"] ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
            var analysisIdFromQuery = query["analysisId"];

            if (string.IsNullOrEmpty(subscriptionId))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("Parâmetro subscriptionId é obrigatório");
                return badResponse;
            }

            var analysisId = !string.IsNullOrWhiteSpace(analysisIdFromQuery)
                ? analysisIdFromQuery.Trim()
                : await ResolveAnalysisIdAsync(subscriptionId, date);

            if (string.IsNullOrWhiteSpace(analysisId))
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Nenhum analysisId encontrado para a subscription/data informada. Informe ?analysisId=...",
                    subscriptionId,
                    date
                });
                return notFoundResponse;
            }
            
            _logger.LogInformation(" [FORCE-CONSOLIDATE] Forçando consolidação para {analysisId}", analysisId);

            // Carrega resultados parciais dos steps
            var storageFindings = await _storageService.LoadStepResultAsync(analysisId, "storage");
            var vmFindings = await _storageService.LoadStepResultAsync(analysisId, "vm");
            var appServiceFindings = await _storageService.LoadStepResultAsync(analysisId, "appservice");
            var functionAppFindings = await _storageService.LoadStepResultAsync(analysisId, "functionapp");
            var logAnalyticsFindings = await _storageService.LoadStepResultAsync(analysisId, "loganalytics");
            var publicIpFindings = await _storageService.LoadStepResultAsync(analysisId, "publicip");

            var allFindings = new List<object>();
            allFindings.AddRange(storageFindings);
            allFindings.AddRange(vmFindings);
            allFindings.AddRange(appServiceFindings);
            allFindings.AddRange(functionAppFindings);
            allFindings.AddRange(logAnalyticsFindings);
            allFindings.AddRange(publicIpFindings);

            _logger.LogInformation(" [FORCE-CONSOLIDATE] Carregados {count} findings de todos os steps", allFindings.Count);

            if (allFindings.Count == 0)
            {
                var conflictResponse = req.CreateResponse(HttpStatusCode.Conflict);
                await conflictResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Consolidação abortada: nenhum finding carregado para o analysisId informado.",
                    analysisId,
                    stepDetails = new
                    {
                        Storage = storageFindings.Count,
                        VM = vmFindings.Count,
                        AppService = appServiceFindings.Count,
                        FunctionApp = functionAppFindings.Count,
                        LogAnalytics = logAnalyticsFindings.Count,
                        PublicIP = publicIpFindings.Count
                    }
                });
                return conflictResponse;
            }

            // Cria resultado final
            var finalResult = new
            {
                AnalysisId = analysisId,
                SubscriptionId = subscriptionId,
                CompletedAt = DateTime.UtcNow,
                TotalFindings = allFindings.Count,
                Findings = allFindings,
                Recommendations = allFindings,
                AnalysisType = "FORCED_CONSOLIDATION",
                StepDetails = new
                {
                    Storage = storageFindings.Count,
                    VM = vmFindings.Count,
                    AppService = appServiceFindings.Count,
                    FunctionApp = functionAppFindings.Count,
                    LogAnalytics = logAnalyticsFindings.Count,
                    PublicIP = publicIpFindings.Count
                }
            };

            // Salva resultado final
            var analysisDate = AnalysisStorageService.TryExtractDateFromAnalysisId(analysisId, out var parsedDate)
                ? parsedDate.Date
                : DateTime.UtcNow.Date;
            await _storageService.SaveAsync(subscriptionId, finalResult, analysisDate);

            var response = req.CreateResponse(HttpStatusCode.OK);
            var result = new
            {
                success = true,
                message = "Consolidação forçada executada com sucesso",
                analysisId = analysisId,
                totalFindings = allFindings.Count,
                stepDetails = finalResult.StepDetails
            };

            await response.WriteAsJsonAsync(result);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [FORCE-CONSOLIDATE] Erro: {error}", ex.Message);
            
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Erro interno ao forçar consolidação." });
            return errorResponse;
        }
    }

    private async Task<string?> ResolveAnalysisIdAsync(string subscriptionId, string date)
    {
        var targetPrefix = $"steps/{subscriptionId}-{date}";
        var container = _storageService.GetContainerClient();
        var candidates = new Dictionary<string, DateTimeOffset?>();

        await foreach (var blob in container.GetBlobsAsync(prefix: targetPrefix))
        {
            var segments = blob.Name.Split('/');
            if (segments.Length < 2)
            {
                continue;
            }

            var candidateId = segments[1];
            if (!candidateId.StartsWith($"{subscriptionId}-{date}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lastModified = blob.Properties.LastModified;
            if (!candidates.TryGetValue(candidateId, out var current) || (lastModified.HasValue && lastModified > current))
            {
                candidates[candidateId] = lastModified;
            }
        }

        return candidates
            .OrderByDescending(kvp => kvp.Value ?? DateTimeOffset.MinValue)
            .Select(kvp => kvp.Key)
            .FirstOrDefault();
    }
}
