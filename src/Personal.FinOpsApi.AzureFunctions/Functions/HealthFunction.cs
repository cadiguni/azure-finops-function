using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Application;
using Personal.FinOpsApi.AzureFunctions.Analyzers;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class CostAnalysisFunctions
{
    private readonly CostAnalysisOrchestrator _orchestrator;
    private readonly FinOpsResultAggregator _resultAggregator;
    private readonly AnalysisStorageService _storageService;
    private readonly ILogger<CostAnalysisFunctions> _logger;

    public CostAnalysisFunctions(
        ILogger<CostAnalysisFunctions> logger, 
        CostAnalysisOrchestrator orchestrator, 
        FinOpsResultAggregator resultAggregator,
        AnalysisStorageService storageService)
    {
        _logger = logger;
        _orchestrator = orchestrator;
        _resultAggregator = resultAggregator;
        _storageService = storageService;
    }

    [Function("health")]
    public HttpResponseData Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.WriteString("✅ FinOps Cost Analysis API");
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
                        
                        _logger.LogInformation($"✅ POST Parsed - SubscriptionId: {request.SubscriptionId}, DryRun: {request.DryRun}, Scope: {request.Scope}");
                        _logger.LogInformation("✅ POST Options: {options}", JsonSerializer.Serialize(request.AnalysisOptions));
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
                // � DEBUG: Log detalhado do GET request
                _logger.LogInformation("🐛 GET Request - iniciando parsing...");
                
                // GET com múltiplas análises habilitadas
                var subscriptionId = req.Query["subscriptionId"] ?? "";
                
                // ✅ Corrigir parsing do dryRun da query string - PROTEÇÃO ANTI-NULL
                var dryRunQuery = req.Query["dryRun"] ?? "";
                
                bool dryRun = !string.IsNullOrEmpty(dryRunQuery) && bool.TryParse(dryRunQuery, out var parsedDryRun) ? parsedDryRun : true; // 🛡️ DEFAULT SAFE
                
                _logger.LogInformation("GET request - subscriptionId: {subscriptionId}, dryRun: {dryRun}", subscriptionId, dryRun);
                
                _logger.LogInformation("🐛 Criando CostAnalysisRequest...");
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
                // 🔥 BLINDAGEM: Garantir que AnalysisOptions nunca seja null
                request.AnalysisOptions ??= new AnalysisIncludeOptions 
                { 
                    UnattachedDisks = true,
                    StorageAccounts = true,
                    UnusedPublicIps = true
                };
                
                // 🔥 REGRA FinOps: GET sem dryRun explícito = sempre dry-run (seguro)
                if (string.IsNullOrEmpty(req.Query["dryRun"]) && req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    request.DryRun = true;
                    _logger.LogInformation("🛡️ GET sem dryRun explícito → forçando dryRun=true (seguro)");
                }
            }

            // Usar orchestrator injetado via DI
            _logger.LogInformation("Iniciando análise com múltiplos analyzers em paralelo...");
            
            // � DEBUG: Verificar se orchestrator está injetado
            _logger.LogInformation("🐛 Orchestrator: {orch}", _orchestrator == null ? "NULL" : "NOT NULL");
            
            // 🛡️ PROTEÇÃO: Modo real ainda não implementado
            if (!request.DryRun)
            {
                _logger.LogWarning("⚠️ EXECUTANDO ANÁLISE EM MODO REAL (dryRun=false) para subscription {sub}", request.SubscriptionId);
                
                // Por enquanto, só analisamos - não executamos ações reais
                _logger.LogInformation("📋 Executando análise em modo somente-leitura (análise + recomendações)");
            }
            
            _logger.LogInformation("🐛 Antes de chamar ExecuteAnalysisAsync");
            var result = await _orchestrator.ExecuteAnalysisAsync(request);
            _logger.LogInformation("🐛 Depois de ExecuteAnalysisAsync - result: {res}", result == null ? "NULL" : "NOT NULL");

            // 📦 OPÇÃO B: Salvar no Storage estruturado (data → subscription)
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
                
                _logger.LogInformation("📦 Resultado salvo em ambos os formatos (OPÇÃO B + legacy)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro ao salvar resultado histórico - continuando execução");
            }

            // �📋 Log de contexto sobre resultados em modo real
            if (!request.DryRun && result.Recommendations.Any())
            {
                _logger.LogInformation("💡 Modo real: {count} recomendações encontradas. Executor de ações ainda não implementado.", result.Recommendations.Count);
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
            _logger.LogError(ex, "🔥 ERRO CAPTURADO - Stack trace completo:");
            
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            errorResponse.Headers.Add("Content-Type", "text/plain");
            
            // 🔥 DEBUG: Stack trace completo para identificar null reference
            await errorResponse.WriteStringAsync(
                $"🔥 STACK TRACE COMPLETO:\n\n{ex.ToString()}"
            );
            
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
                message = ex.Message,
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
    /// 🔧 FORÇAR CONSOLIDAÇÃO - Para testar consolidação manual de steps
    /// </summary>
    [Function("force-consolidate")]
    public async Task<HttpResponseData> ForceConsolidate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var subscriptionId = query["subscriptionId"];
            var date = query["date"] ?? DateTime.UtcNow.ToString("yyyy-MM-dd");

            if (string.IsNullOrEmpty(subscriptionId))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("Parâmetro subscriptionId é obrigatório");
                return badResponse;
            }

            var analysisId = $"{subscriptionId}-{date}";
            
            _logger.LogInformation("🔧 [FORCE-CONSOLIDATE] Forçando consolidação para {analysisId}", analysisId);

            // Carrega resultados parciais dos steps
            var storageFindings = await _storageService.LoadStepResultAsync(analysisId, "storage");
            var vmFindings = await _storageService.LoadStepResultAsync(analysisId, "vm");
            var appServiceFindings = await _storageService.LoadStepResultAsync(analysisId, "appservice");
            var publicIpFindings = await _storageService.LoadStepResultAsync(analysisId, "publicip");

            var allFindings = new List<object>();
            allFindings.AddRange(storageFindings);
            allFindings.AddRange(vmFindings);
            allFindings.AddRange(appServiceFindings);
            allFindings.AddRange(publicIpFindings);

            _logger.LogInformation("🔧 [FORCE-CONSOLIDATE] Carregados {count} findings de todos os steps", allFindings.Count);

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
                    PublicIP = publicIpFindings.Count
                }
            };

            // Salva resultado final
            await _storageService.SaveAsync(subscriptionId, finalResult, DateTime.UtcNow);

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
            _logger.LogError(ex, "❌ [FORCE-CONSOLIDATE] Erro: {error}", ex.Message);
            
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }
}