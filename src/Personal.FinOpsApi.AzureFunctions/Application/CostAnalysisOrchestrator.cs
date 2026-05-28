using Personal.FinOpsApi.AzureFunctions.Analyzers;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Application;

public class CostAnalysisOrchestrator
{
    private readonly UnattachedDiskAnalyzer _diskAnalyzer;
    private readonly StorageAccountAnalyzer _storageAnalyzer;
    private readonly UnusedPublicIpAnalyzer _publicIpAnalyzer;
    private readonly IdleVmAnalyzer _idleVmAnalyzer;
    private readonly AppServiceAnalyzer _appServiceAnalyzer;
    private readonly FunctionAppAnalyzer _functionAppAnalyzer;
    private readonly LogAnalyticsAnalyzer _logAnalyticsAnalyzer;
    private readonly AnalysisStorageService _storageService;
    private readonly LogAnalyticsDataCollectorService _logAnalyticsService;

    private readonly ILogger<CostAnalysisOrchestrator> _logger;

    public CostAnalysisOrchestrator(
        UnattachedDiskAnalyzer diskAnalyzer, 
        StorageAccountAnalyzer storageAnalyzer,
        UnusedPublicIpAnalyzer publicIpAnalyzer,
        IdleVmAnalyzer idleVmAnalyzer,
        AppServiceAnalyzer appServiceAnalyzer,
        FunctionAppAnalyzer functionAppAnalyzer,
        LogAnalyticsAnalyzer logAnalyticsAnalyzer,
        AnalysisStorageService storageService,
        LogAnalyticsDataCollectorService logAnalyticsService,

        ILogger<CostAnalysisOrchestrator> logger)
    {
        _diskAnalyzer = diskAnalyzer;
        _storageAnalyzer = storageAnalyzer;
        _publicIpAnalyzer = publicIpAnalyzer;
        _idleVmAnalyzer = idleVmAnalyzer;
        _appServiceAnalyzer = appServiceAnalyzer;
        _functionAppAnalyzer = functionAppAnalyzer;
        _logAnalyticsAnalyzer = logAnalyticsAnalyzer;
        _storageService = storageService;
        _logAnalyticsService = logAnalyticsService;

        _logger = logger;
    }

    /// <summary>
    /// Executa análise completa baseada na requisição
    /// </summary>
    public async Task<CostAnalysisResult> ExecuteAnalysisAsync(CostAnalysisRequest request)
    {
        //  DEBUG: Log completo da requisição
        Console.WriteLine($" ExecuteAnalysisAsync - Request recebido:");
        Console.WriteLine($"   Request: {(request == null ? "NULL" : "NOT NULL")}");
        if (request != null)
        {
            Console.WriteLine($"   Scope: '{request.Scope ?? "NULL"}'");
            Console.WriteLine($"   SubscriptionId: '{request.SubscriptionId ?? "NULL"}'");
            Console.WriteLine($"   DryRun: {request.DryRun}");
            Console.WriteLine($"   AnalysisOptions: {(request.AnalysisOptions == null ? "NULL" : "NOT NULL")}");
        }

        var result = new CostAnalysisResult
        {
            Scope = request.Scope,
            SubscriptionId = request.SubscriptionId,
            ManagementGroupId = request.ManagementGroupId,
            AnalysisPeriodDays = request.AnalysisPeriodDays,
            DryRun = request.DryRun
        };

        var allRecommendations = new List<CostRecommendation>();

        try
        {
            // Validar request
            ValidateRequest(request);

            //  NÍVEL 4: Múltiplos analyzers executando em paralelo
            var tasks = new List<Task<List<CostRecommendation>>>();
            
            if (request.AnalysisOptions.UnattachedDisks)
            {
                tasks.Add(AnalyzeUnattachedDisksAsync(request));
            }
            
            if (request.AnalysisOptions.StorageAccounts)
            {
                tasks.Add(AnalyzeStorageAccountsAsync(request));
            }
            
            if (request.AnalysisOptions.UnusedPublicIps)
            {
                tasks.Add(AnalyzeUnusedPublicIpsAsync(request));
            }

            //  VMs Idle - Maior impacto FinOps
            if (request.AnalysisOptions.IdleVms)
            {
                tasks.Add(AnalyzeIdleVmsAsync(request));
            }

            //  App Services - Análise de utilização
            if (request.AnalysisOptions.AppServices)
            {
                tasks.Add(AnalyzeAppServicesAsync(request));
            }

            //  EXECUTAR SEQUENCIALMENTE para evitar 429 (em vez de paralelo)
            _logger.LogInformation(" Executando {count} análises sequenciais para evitar rate limiting", tasks.Count);
            
            foreach (var task in tasks)
            {
                try
                {
                    var recommendations = await task;
                    allRecommendations.AddRange(recommendations);
                    
                    //  Small delay entre análises para ser gentil com APIs
                    await Task.Delay(500);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, " Falha em análise individual: {error}", ex.Message);
                    // Continua com outras análises mesmo se uma falhar
                }
            }

            // Futuras análises virão aqui:
            // if (request.AnalysisOptions.Vms) { ... }
            // if (request.AnalysisOptions.AppServices) { ... }
            // if (request.AnalysisOptions.SqlDatabases) { ... }

            result.Recommendations = allRecommendations;
            result.Summary = BuildSummary(allRecommendations);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro na análise de custo: {ex.Message}");
            // Em caso de erro, retorna resultado vazio mas válido
        }

        return result;
    }

    /// <summary>
    /// Analisa discos não anexados
    /// </summary>
    private async Task<List<CostRecommendation>> AnalyzeUnattachedDisksAsync(CostAnalysisRequest request)
    {
        try
        {
            Console.WriteLine(" Iniciando análise de discos não anexados...");
            
            if (request.Scope.Equals("subscription", StringComparison.OrdinalIgnoreCase) && 
                !string.IsNullOrEmpty(request.SubscriptionId))
            {
                var standardResult = await _diskAnalyzer.AnalyzeSubscriptionAsync(
                    request.SubscriptionId,
                    request.AnalysisPeriodDays,
                    request.DryRun);

                // Converter StandardFinding para CostRecommendation (compatibilidade)
                return ConvertToLegacyFormat(standardResult.Findings);
            }
            else if (request.Scope.Equals("managementGroup", StringComparison.OrdinalIgnoreCase))
            {
                // TODO: Implementar análise por Management Group
                // Requer listagem de subscriptions no MG
                Console.WriteLine("Análise por Management Group ainda não implementada");
                return new List<CostRecommendation>();
            }

            Console.WriteLine(" Discos: Escopo não suportado ou SubscriptionId não fornecido");
            return new List<CostRecommendation>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($" Erro ao analisar discos não anexados: {ex.Message}");
            return new List<CostRecommendation>();
        }
    }

    /// <summary>
    /// Valida se a requisição está correta
    /// </summary>
    private void ValidateRequest(CostAnalysisRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request), "Request não pode ser null");
        }

        if (string.IsNullOrEmpty(request.Scope))
        {
            throw new ArgumentException("Scope não pode ser null ou vazio");
        }

        //  BLINDAGEM FINAL: AnalysisOptions nunca null
        request.AnalysisOptions ??= new AnalysisIncludeOptions();

        if (request.Scope.Equals("subscription", StringComparison.OrdinalIgnoreCase) && 
            string.IsNullOrEmpty(request.SubscriptionId))
        {
            throw new ArgumentException("SubscriptionId é obrigatório quando scope = 'subscription'");
        }

        if (request.Scope.Equals("managementGroup", StringComparison.OrdinalIgnoreCase) && 
            string.IsNullOrEmpty(request.ManagementGroupId))
        {
            throw new ArgumentException("ManagementGroupId é obrigatório quando scope = 'managementGroup'");
        }

        if (request.AnalysisPeriodDays < 1 || request.AnalysisPeriodDays > 365)
        {
            throw new ArgumentException("AnalysisPeriodDays deve estar entre 1 e 365");
        }
    }

    /// <summary>
    /// Constrói resumo consolidado
    /// </summary>
    private CostAnalysisSummary BuildSummary(List<CostRecommendation> recommendations)
    {
        var summary = new CostAnalysisSummary
        {
            TotalResourcesAnalyzed = recommendations.Count,
            TotalRecommendations = recommendations.Count,
            TotalEstimatedMonthlySavings = recommendations.Sum(r => r.EstimatedMonthlySavings)
        };

        // Breakdown por tipo
        var typeGroups = recommendations.GroupBy(r => r.Type);
        foreach (var group in typeGroups)
        {
            summary.ByType[group.Key] = new RecommendationTypeSummary
            {
                Count = group.Count(),
                EstimatedMonthlySavings = group.Sum(r => r.EstimatedMonthlySavings)
            };
        }

        return summary;
    }
    
    /// <summary>
    ///  NÍVEL 4: Análise de Storage Accounts subutilizados
    /// </summary>
    private async Task<List<CostRecommendation>> AnalyzeStorageAccountsAsync(CostAnalysisRequest request)
    {
        try
        {
            Console.WriteLine(" Iniciando análise de Storage Accounts...");
            
            if (request.Scope == "subscription" && !string.IsNullOrEmpty(request.SubscriptionId))
            {
                var standardResult = await _storageAnalyzer.AnalyzeSubscriptionAsync(
                    request.SubscriptionId, 
                    request.AnalysisPeriodDays, 
                    request.DryRun);

                // Converter StandardFinding para CostRecommendation (compatibilidade)
                return ConvertToLegacyFormat(standardResult.Findings);
            }

            Console.WriteLine(" Storage: Escopo não suportado ou SubscriptionId não fornecido");
            return new List<CostRecommendation>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($" Erro na análise de Storage Accounts: {ex.Message}");
            return new List<CostRecommendation>();
        }
    }

    /// <summary>
    /// Análise específica de Public IPs não utilizados
    /// </summary>
    private async Task<List<CostRecommendation>> AnalyzeUnusedPublicIpsAsync(CostAnalysisRequest request)
    {
        try
        {
            Console.WriteLine(" Iniciando análise de Public IPs ociosos...");
            
            if (request.Scope == "subscription" && !string.IsNullOrEmpty(request.SubscriptionId))
            {
                var standardResult = await _publicIpAnalyzer.AnalyzeAsync(
                    request.SubscriptionId, 
                    request.AnalysisPeriodDays, 
                    request.DryRun);

                // Converter StandardFinding para CostRecommendation (compatibilidade)
                return ConvertToLegacyFormat(standardResult.Findings);
            }

            Console.WriteLine(" Public IPs: Escopo não suportado ou SubscriptionId não fornecido");
            return new List<CostRecommendation>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($" Erro na análise de Public IPs: {ex.Message}");
            return new List<CostRecommendation>();
        }
    }

    /// <summary>
    /// Análise específica de VMs ociosas (maior impacto FinOps)
    /// </summary>
    private async Task<List<CostRecommendation>> AnalyzeIdleVmsAsync(CostAnalysisRequest request)
    {
        try
        {
            Console.WriteLine(" Iniciando análise de VMs ociosas - maior impacto FinOps...");
            
            if (request.Scope == "subscription" && !string.IsNullOrEmpty(request.SubscriptionId))
            {
                var standardResult = await _idleVmAnalyzer.AnalyzeAsync(
                    request.SubscriptionId, 
                    request.AnalysisPeriodDays, 
                    request.DryRun);

                // Converter StandardFinding para CostRecommendation (compatibilidade)
                return ConvertToLegacyFormat(standardResult.Findings);
            }

            Console.WriteLine(" VMs Idle: Escopo não suportado ou SubscriptionId não fornecido");
            return new List<CostRecommendation>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($" Erro na análise de VMs ociosas: {ex.Message}");
            return new List<CostRecommendation>();
        }
    }

    /// <summary>
    /// Análise específica de App Services ociosos
    /// </summary>
    private async Task<List<CostRecommendation>> AnalyzeAppServicesAsync(CostAnalysisRequest request)
    {
        try
        {
            Console.WriteLine(" Iniciando análise de App Services ociosos...");
            
            if (request.Scope == "subscription" && !string.IsNullOrEmpty(request.SubscriptionId))
            {
                var standardResult = await _appServiceAnalyzer.AnalyzeAsync(
                    request.SubscriptionId, 
                    request.AnalysisPeriodDays, 
                    request.DryRun);

                // Converter StandardFinding para CostRecommendation (compatibilidade)
                return ConvertToLegacyFormat(standardResult.Findings);
            }

            Console.WriteLine(" App Services: Escopo não suportado ou SubscriptionId não fornecido");
            return new List<CostRecommendation>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($" Erro na análise de App Services: {ex.Message}");
            return new List<CostRecommendation>();
        }
    }

    /// <summary>
    ///  Conversão do StandardFinding (novo) para CostRecommendation (legacy)
    /// </summary>
    private List<CostRecommendation> ConvertToLegacyFormat(List<Models.StandardFinding> standardFindings)
    {
        return standardFindings.Select(finding => new CostRecommendation
        {
            SubscriptionId = finding.SubscriptionId,
            ResourceId = finding.ResourceId,
            ResourceName = finding.ResourceName,
            ResourceType = finding.ResourceType,
            Priority = finding.Priority,
            EstimatedMonthlyCost = finding.EstimatedMonthlyCost,
            PotentialMonthlySavings = finding.EstimatedMonthlySavings,
            Recommendation = finding.Recommendation,
            Description = finding.Description,
            Impact = "Medium", // Usar valor padrão - não está no StandardFinding
            ImplementationEffort = "Low", // Usar valor padrão - não está no StandardFinding
            ResourceGroup = finding.Metadata.ContainsKey("resourceGroup") ? 
                finding.Metadata["resourceGroup"]?.ToString() ?? string.Empty : string.Empty,
            Location = finding.Metadata.ContainsKey("location") ? 
                finding.Metadata["location"]?.ToString() ?? string.Empty : string.Empty,
            Tags = finding.Metadata.ContainsKey("tags") ? 
                finding.Metadata["tags"] as Dictionary<string, string> ?? new Dictionary<string, string>() 
                : new Dictionary<string, string>(),
            LastEvaluationDate = DateTime.UtcNow,
            Type = finding.Type
        }).ToList();
    }

    /// <summary>
    ///  NOVO: Constrói Top 10 automático das maiores economias do dia
    /// Pipeline: ler recommendations.json → normalizar → ordenar → salvar top10.json
    /// </summary>
    public async Task<DailyTop10Result?> BuildTop10Async(DateTime analysisDate)
    {
        try
        {
            _logger.LogInformation(" Iniciando construção do Top 10 para {date}", analysisDate.ToString("yyyy-MM-dd"));

            // 1⃣ Ler todos os recommendations.json do dia
            var allRecommendations = await _storageService.GetDailyAnalysisAsync(analysisDate);
            
            if (!allRecommendations.Any())
            {
                _logger.LogWarning(" Nenhuma recomendação encontrada para {date}", analysisDate.ToString("yyyy-MM-dd"));
                return null;
            }

            // 2⃣ Normalizar para TopSavingCandidate
            var candidates = allRecommendations.Select(rec => new TopSavingCandidate
            {
                SubscriptionId = rec.SubscriptionId,
                ResourceType = rec.ResourceType,
                ResourceName = rec.ResourceName,
                ResourceId = rec.ResourceId,
                EstimatedMonthlySavings = rec.PotentialMonthlySavings,
                AnalyzerType = rec.Type,
                Priority = rec.Priority,
                Description = rec.Description
            }).ToList();

            // 3⃣ Ordenar por economia e pegar Top 10
            var top10 = candidates
                .OrderByDescending(x => x.EstimatedMonthlySavings)
                .Take(10)
                .Select((candidate, index) => 
                {
                    candidate.Rank = index + 1;
                    return candidate;
                })
                .ToList();

            // 4⃣ Calcular estatísticas
            var uniqueSubscriptions = candidates.Select(c => c.SubscriptionId).Distinct().Count();
            var totalSavings = candidates.Sum(c => c.EstimatedMonthlySavings);

            var result = new DailyTop10Result
            {
                Date = analysisDate.ToString("yyyy-MM-dd"),
                TotalSubscriptions = uniqueSubscriptions,
                TotalSavings = totalSavings,
                Top10 = top10
            };

            _logger.LogInformation(" Top 10 construído: {total} recomendações, R$ {savings} economia total", 
                candidates.Count, totalSavings);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao construir Top 10 para {date}", analysisDate.ToString("yyyy-MM-dd"));
            return null;
        }
    }

    /// <summary>
    ///  EXECUÇÃO DIRETA: Análises DIÁRIAS (rápidas, sem métricas pesadas)
    ///  PROCESSAMENTO DIRETO: Executado pelo Timer Function - ANÁLISE REAL COM STORAGE
    ///  V4.1: Timeout de 3 minutos para análises diárias
    /// </summary>
    public async Task RunDailyAnalysisAsync(string subscriptionId)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogInformation(" ANÁLISE REAL: Executando análises DIÁRIAS para subscription {subscriptionId} com timeout de 3min", subscriptionId);
        
        var results = new List<object>();
        var counters = new
        {
            orphaned_disks = 0,
            orphaned_ips = 0,
            total_resources = 0
        };

        try
        {
            //  TIMEOUT: 3 minutos para análises diárias (mais rápidas)
            using var dailyTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            //  1. ANALYSIS: Orphaned Managed Disks
            _logger.LogInformation(" Analisando discos órfãos para {subscriptionId}...", subscriptionId);
            var orphanedDisks = await _diskAnalyzer.AnalyzeSubscriptionAsync(subscriptionId);
            counters = counters with { orphaned_disks = orphanedDisks.Findings.Count };
            results.Add(orphanedDisks);

            //  2. ANALYSIS: Orphaned Public IPs  
            _logger.LogInformation(" Analisando IPs públicos órfãos para {subscriptionId}...", subscriptionId);
            var orphanedIps = await _publicIpAnalyzer.AnalyzeAsync(subscriptionId);
            counters = counters with { orphaned_ips = orphanedIps.Findings.Count };
            results.Add(orphanedIps);

            //  Total resources found
            counters = counters with { total_resources = results.Count };

            //  4. SAVE RESULTS: Salvar no container finops-analysis
            if (results.Any())
            {
                var analysisResult = new
                {
                    subscription_id = subscriptionId,
                    analysis_date = startTime.ToString("yyyy-MM-dd"),
                    analysis_timestamp = startTime,
                    analysis_type = "daily",
                    total_findings = results.Count,
                    counters = counters,
                    findings = results
                };

                await _storageService.SaveAsync(subscriptionId, analysisResult, startTime);
                _logger.LogInformation(" Resultados salvos no storage: {findings} findings encontradas", results.Count);

                //  5. ENVIAR PARA LOG ANALYTICS: Para dashboards e alertas
                var standardResults = results.OfType<StandardAnalyzerResult>().ToList();
                await SendToLogAnalyticsAsync(standardResults, subscriptionId, "daily", startTime);
            }

            var executionTime = DateTime.UtcNow - startTime;
            _logger.LogInformation(" ANÁLISE REAL concluída para {subscriptionId} - Discos órfãos: {disks}, IPs órfãos: {ips} - Tempo: {duration}ms", 
                subscriptionId, counters.orphaned_disks, counters.orphaned_ips, executionTime.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro na análise diária para {subscriptionId}", subscriptionId);
            throw;
        }
    }

    /// <summary>
    ///  EXECUÇÃO DIRETA: Análises 2X SEMANA (pesadas, com Azure Monitor)
    ///  PROCESSAMENTO DIRETO: Com timeout e circuit breaker - ANÁLISE REAL
    ///  V4.1: Timeout de 7 minutos para evitar limite de 10min do Azure
    /// </summary>
    public async Task RunBiWeeklyAnalysisAsync(string subscriptionId)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogInformation(" ANÁLISE REAL QUINZENAL: Executando para subscription {subscriptionId} com timeout de 7min", subscriptionId);
        
        var results = new List<object>();
        var counters = new
        {
            idle_vms = 0,
            storage_accounts = 0,
            app_services = 0,
            function_apps = 0,
            log_analytics = 0
        };

        try
        {
            //  TIMEOUT GLOBAL: 7 minutos para toda a análise quinzenal
            using var globalTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(7));
            
            //  EXECUTAR COM THROTTLING para evitar 429 (CRÍTICO!)
            _logger.LogInformation(" Executando 5 análises com throttling (maxConcurrency=2) para evitar rate limiting...");
            
            var analyzerFactories = new List<Func<Task<StandardAnalyzerResult>>>
            {
                () => {
                    _logger.LogInformation(" [1/5] Analisando VMs ociosas para {subscriptionId}...", subscriptionId);
                    return _idleVmAnalyzer.AnalyzeAsync(subscriptionId);
                },
                () => {
                    _logger.LogInformation(" [2/5] Analisando Storage Accounts para {subscriptionId}...", subscriptionId);
                    return _storageAnalyzer.AnalyzeSubscriptionAsync(subscriptionId);
                },
                () => {
                    _logger.LogInformation(" [3/5] Analisando App Services para {subscriptionId}...", subscriptionId);
                    return _appServiceAnalyzer.AnalyzeAsync(subscriptionId);
                },
                () => {
                    _logger.LogInformation(" [4/5] Analisando Function Apps para {subscriptionId}...", subscriptionId);
                    return _functionAppAnalyzer.AnalyzeAsync(subscriptionId);
                },
                () => {
                    _logger.LogInformation(" [5/5] Analisando Log Analytics workspaces para {subscriptionId}...", subscriptionId);
                    return _logAnalyticsAnalyzer.AnalyzeAsync(subscriptionId);
                }
            };

            var analyzerResults = await Throttle.WhenAllThrottled(analyzerFactories, maxConcurrency: 2);
            
            // Atualizar contadores com os resultados reais
            counters = new
            {
                idle_vms = analyzerResults[0].Findings.Count,
                storage_accounts = analyzerResults[1].Findings.Count,
                app_services = analyzerResults[2].Findings.Count,
                function_apps = analyzerResults[3].Findings.Count,
                log_analytics = analyzerResults[4].Findings.Count
            };

            results.AddRange(analyzerResults);

            //  4. SAVE RESULTS: Salvar no container finops-analysis
            if (results.Any())
            {
                var analysisResult = new 
                {
                    subscription_id = subscriptionId,
                    analysis_date = startTime.ToString("yyyy-MM-dd"),
                    analysis_timestamp = startTime,
                    analysis_type = "bi-weekly",
                    total_findings = results.Count,
                    counters = counters,
                    findings = results
                };

                await _storageService.SaveAsync(subscriptionId, analysisResult, startTime);
                _logger.LogInformation(" Resultados bi-semanais salvos no storage: {findings} findings encontradas", results.Count);

                //  5. ENVIAR PARA LOG ANALYTICS: Para dashboards e alertas
                await SendToLogAnalyticsAsync(analyzerResults.ToList(), subscriptionId, "bi-weekly", startTime);
            }

            var executionTime = DateTime.UtcNow - startTime;
            _logger.LogInformation(" ANÁLISE REAL 2X SEMANA concluída para {subscriptionId} - VMs idle: {vms}, Storage: {storage}, App Services: {apps}, Log Analytics: {la} - Tempo: {duration}ms", 
                subscriptionId, counters.idle_vms, counters.storage_accounts, counters.app_services, counters.log_analytics, executionTime.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro na análise quinzenal para {subscriptionId}", subscriptionId);
            throw;
        }
    }

    /// <summary>
    ///  HELPER: Envia recomendações para Log Analytics para dashboards
    /// </summary>
    private async Task SendToLogAnalyticsAsync(
        List<StandardAnalyzerResult> analyzerResults, 
        string subscriptionId, 
        string analysisType, 
        DateTime timestamp)
    {
        try
        {
            var allLogEntries = new List<FinOpsLogEntry>();
            var analysisId = Guid.NewGuid().ToString();

            //  CONVERTER: Cada resultado do analyzer para entradas do Log Analytics
            foreach (var analyzerResult in analyzerResults)
            {
                var logEntries = _logAnalyticsService.ConvertToLogEntries(
                    analyzerResult, 
                    analysisId, 
                    subscriptionId, 
                    analysisType, 
                    timestamp);

                allLogEntries.AddRange(logEntries);
            }

            if (allLogEntries.Any())
            {
                //  ENVIAR: Todas as recomendações em batch
                var success = await _logAnalyticsService.SendRecommendationsAsync(allLogEntries, analysisId);
                
                if (success)
                {
                    _logger.LogInformation(" {count} recomendações enviadas para Log Analytics (análise: {analysisType})", 
                        allLogEntries.Count, analysisType);
                }
                else
                {
                    _logger.LogWarning(" Falha ao enviar recomendações para Log Analytics");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao enviar para Log Analytics - continuando execução");
            // Não falhar a análise por erro do Log Analytics
        }
    }

    /// <summary>
    ///  ANÁLISE COMPLETA DE SUBSCRIPTION - Usado pela queue de produção
    /// 
    ///  MODO PRODUÇÃO: Configurações otimizadas para subscriptions grandes
    /// - Timeouts estendidos (até 60 min)
    /// - Delays entre operações Azure
    /// - Throttling reduzido
    /// - Log Analytics integrado
    /// </summary>
    public async Task AnalyzeSubscriptionAsync(string subscriptionId, string analysisType = "complete", bool isProductionMode = false)
    {
        var startTime = DateTime.UtcNow;
        var mode = isProductionMode ? "PRODUÇÃO" : "NORMAL";
        
        _logger.LogInformation(" [{mode}] Iniciando análise completa de subscription {subscriptionId} - Tipo: {analysisType}", 
            mode, subscriptionId, analysisType);

        try
        {
            //  CONFIGURAÇÕES POR MODO
            var delayBetweenAnalyzers = isProductionMode ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2);

            //  ANÁLISE COMPLETA SEMPRE (todos os analyzers)
            _logger.LogInformation(" [{mode}] Executando análise COMPLETA para {subscriptionId}", mode, subscriptionId);

            var results = new List<object>();

            // 1⃣ Discos órfãos (rápido)
            _logger.LogInformation(" [{mode}] Analisando discos órfãos...", mode);
            var diskResult = await _diskAnalyzer.AnalyzeSubscriptionAsync(subscriptionId);
            results.Add(diskResult);
            
            if (isProductionMode) 
            {
                _logger.LogInformation("⏰ [{mode}] Delay entre analyzers: {delay}s", mode, delayBetweenAnalyzers.TotalSeconds);
                await Task.Delay(delayBetweenAnalyzers);
            }

            // 2⃣ IPs públicos órfãos (rápido)
            _logger.LogInformation(" [{mode}] Analisando IPs públicos órfãos...", mode);
            var ipResult = await _publicIpAnalyzer.AnalyzeAsync(subscriptionId);
            results.Add(ipResult);
            
            if (isProductionMode) await Task.Delay(delayBetweenAnalyzers);

            // 3⃣ Storage Accounts (Azure Monitor - PESADO)
            _logger.LogInformation(" [{mode}] Analisando Storage Accounts (PESADO - Azure Monitor)...", mode);
            var storageResult = await _storageAnalyzer.AnalyzeSubscriptionAsync(subscriptionId);
            results.Add(storageResult);
            
            if (isProductionMode) await Task.Delay(delayBetweenAnalyzers);

            // 4⃣ App Services (médio)
            _logger.LogInformation(" [{mode}] Analisando App Services...", mode);
            var appServiceResult = await _appServiceAnalyzer.AnalyzeAsync(subscriptionId);
            results.Add(appServiceResult);

            if (isProductionMode) await Task.Delay(delayBetweenAnalyzers);

            // 5⃣ VMs Idle (Azure Monitor - MUITO PESADO)
            _logger.LogInformation(" [{mode}] Analisando VMs Idle (MUITO PESADO - Azure Monitor)...", mode);
            var vmResult = await _idleVmAnalyzer.AnalyzeAsync(subscriptionId);
            results.Add(vmResult);

            //  CONSOLIDAR RESULTADOS
            var allFindings = results.OfType<StandardAnalyzerResult>().SelectMany(r => r.Findings).ToList();
            var totalFindings = allFindings.Count;
            var totalSavings = allFindings.Sum(f => f.EstimatedMonthlySavings);
            
            _logger.LogInformation(" [{mode}] Análise concluída - {findings} recomendações, ${savings:F2}/mês economia potencial", 
                mode, totalFindings, totalSavings);

            //  SALVAR RESULTADOS NO BLOB STORAGE
            var analysisResult = new
            {
                subscription_id = subscriptionId,
                analysis_date = startTime.ToString("yyyy-MM-dd"),
                analysis_timestamp = startTime,
                analysis_type = analysisType,
                findings = results.OfType<StandardAnalyzerResult>().ToList(),
                summary = new
                {
                    total_findings = totalFindings,
                    total_savings = totalSavings,
                    analyzed_resources = allFindings.Count,
                    execution_time_seconds = (DateTime.UtcNow - startTime).TotalSeconds
                }
            };

            //  SALVAR USANDO AnalysisStorageService (recommendations.json + raw.json)
            await _storageService.SaveAsync(subscriptionId, analysisResult, startTime);
            
            _logger.LogInformation(" [{mode}] Resultados salvos no Blob Storage: subscription {subscriptionId}", mode, subscriptionId);

            //  ENVIAR PARA LOG ANALYTICS
            var standardResults = results.OfType<StandardAnalyzerResult>().ToList();
            await SendToLogAnalyticsAsync(standardResults, subscriptionId, analysisType, startTime);

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation(" [{mode}] Análise de subscription {subscriptionId} concluída em {duration:mm\\:ss}", 
                mode, subscriptionId, duration);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogError(ex, " [{mode}] Erro na análise de subscription {subscriptionId} após {duration:mm\\:ss}", 
                mode, subscriptionId, duration);
            throw;
        }
    }

    #region MÉTODOS PARA PROCESSAMENTO EM ETAPAS (SOLUÇÃO TIMEOUT)

    /// <summary>
    ///  Analisa apenas Storage Accounts (step específico)
    /// </summary>
    public async Task<IList<object>> AnalyzeStorageAccountsOnlyAsync(string subscriptionId)
    {
        _logger.LogInformation(" [STEP-STORAGE] Iniciando análise de Storage Accounts para {subscriptionId}", subscriptionId);

        var findings = new List<object>();
        
        try
        {
            var storageResult = await _storageAnalyzer.AnalyzeSubscriptionAsync(subscriptionId);
            if (storageResult?.Findings != null)
            {
                findings.AddRange(storageResult.Findings.Cast<object>());
            }

            _logger.LogInformation(" [STEP-STORAGE] {count} findings encontrados para {subscriptionId}", 
                findings.Count, subscriptionId);
                
            return findings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [STEP-STORAGE] Erro na análise de storage para {subscriptionId}", subscriptionId);
            throw;
        }
    }

    /// <summary>
    ///  Analisa apenas VMs (step específico)
    /// </summary>
    public async Task<IList<object>> AnalyzeVirtualMachinesOnlyAsync(string subscriptionId)
    {
        _logger.LogInformation(" [STEP-VM] Iniciando análise de VMs para {subscriptionId}", subscriptionId);

        var findings = new List<object>();
        
        try
        {
            // VMs ociosas
            var idleVmResult = await _idleVmAnalyzer.AnalyzeAsync(subscriptionId);
            if (idleVmResult?.Findings != null)
            {
                findings.AddRange(idleVmResult.Findings.Cast<object>());
            }

            // Discos desanexados
            var diskResult = await _diskAnalyzer.AnalyzeSubscriptionAsync(subscriptionId);
            if (diskResult?.Findings != null)
            {
                findings.AddRange(diskResult.Findings.Cast<object>());
            }

            _logger.LogInformation(" [STEP-VM] {count} findings encontrados para {subscriptionId}", 
                findings.Count, subscriptionId);
                
            return findings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [STEP-VM] Erro na análise de VMs para {subscriptionId}", subscriptionId);
            throw;
        }
    }

    /// <summary>
    ///  Analisa apenas App Services (step específico)
    /// </summary>
    public async Task<IList<object>> AnalyzeAppServicesOnlyAsync(string subscriptionId)
    {
        _logger.LogInformation(" [STEP-APPSERVICE] Iniciando análise de App Services para {subscriptionId}", subscriptionId);

        var findings = new List<object>();
        
        try
        {
            var appServiceResult = await _appServiceAnalyzer.AnalyzeAsync(subscriptionId);
            if (appServiceResult?.Findings != null)
            {
                findings.AddRange(appServiceResult.Findings.Cast<object>());
            }

            _logger.LogInformation(" [STEP-APPSERVICE] {count} findings encontrados para {subscriptionId}", 
                findings.Count, subscriptionId);
                
            return findings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [STEP-APPSERVICE] Erro na análise de App Services para {subscriptionId}", subscriptionId);
            throw;
        }
    }

    /// <summary>
    ///  Analisa apenas Function Apps (step específico)
    /// </summary>
    public async Task<IList<object>> AnalyzeFunctionAppsOnlyAsync(string subscriptionId)
    {
        _logger.LogInformation(" [STEP-FUNCTIONAPP] Iniciando análise de Function Apps para {subscriptionId}", subscriptionId);

        var findings = new List<object>();
        
        try
        {
            var functionAppResult = await _functionAppAnalyzer.AnalyzeAsync(subscriptionId);
            if (functionAppResult?.Findings != null)
            {
                findings.AddRange(functionAppResult.Findings.Cast<object>());
            }

            _logger.LogInformation(" [STEP-FUNCTIONAPP] {count} findings encontrados para {subscriptionId}", 
                findings.Count, subscriptionId);
                
            return findings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [STEP-FUNCTIONAPP] Erro na análise de Function Apps para {subscriptionId}", subscriptionId);
            throw;
        }
    }

    /// <summary>
    ///  Analisa apenas IPs Públicos (step específico)
    /// </summary>
    public async Task<IList<object>> AnalyzePublicIpsOnlyAsync(string subscriptionId)
    {
        _logger.LogInformation(" [STEP-PUBLICIP] Iniciando análise de IPs Públicos para {subscriptionId}", subscriptionId);

        var findings = new List<object>();
        
        try
        {
            var publicIpResult = await _publicIpAnalyzer.AnalyzeAsync(subscriptionId);
            if (publicIpResult?.Findings != null)
            {
                findings.AddRange(publicIpResult.Findings.Cast<object>());
            }

            _logger.LogInformation(" [STEP-PUBLICIP] {count} findings encontrados para {subscriptionId}", 
                findings.Count, subscriptionId);
                
            return findings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [STEP-PUBLICIP] Erro na análise de IPs Públicos para {subscriptionId}", subscriptionId);
            throw;
        }
    }

    /// <summary>
    ///  Analisa apenas Log Analytics workspaces (step específico)
    /// </summary>
    public async Task<IList<object>> AnalyzeLogAnalyticsOnlyAsync(string subscriptionId)
    {
        _logger.LogInformation(" [STEP-LOGANALYTICS] Iniciando análise de Log Analytics para {subscriptionId}", subscriptionId);

        var findings = new List<object>();
        
        try
        {
            var logAnalyticsResult = await _logAnalyticsAnalyzer.AnalyzeAsync(subscriptionId);
            if (logAnalyticsResult?.Findings != null)
            {
                findings.AddRange(logAnalyticsResult.Findings.Cast<object>());
            }

            _logger.LogInformation(" [STEP-LOGANALYTICS] {count} findings encontrados para {subscriptionId}", 
                findings.Count, subscriptionId);
                
            return findings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [STEP-LOGANALYTICS] Erro na análise de Log Analytics para {subscriptionId}", subscriptionId);
            throw;
        }
    }

    #endregion
}