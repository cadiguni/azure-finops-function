using Personal.FinOpsApi.AzureFunctions.Analyzers;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Application;

public class CostAnalysisOrchestrator
{
    private readonly UnattachedDiskAnalyzer _diskAnalyzer;
    private readonly StorageAccountAnalyzer _storageAnalyzer;
    private readonly UnusedPublicIpAnalyzer _publicIpAnalyzer;
    private readonly IdleVmAnalyzer _idleVmAnalyzer;
    private readonly AppServiceAnalyzer _appServiceAnalyzer;
    private readonly AnalysisStorageService _storageService;
    private readonly ILogger<CostAnalysisOrchestrator> _logger;

    public CostAnalysisOrchestrator(
        UnattachedDiskAnalyzer diskAnalyzer, 
        StorageAccountAnalyzer storageAnalyzer,
        UnusedPublicIpAnalyzer publicIpAnalyzer,
        IdleVmAnalyzer idleVmAnalyzer,
        AppServiceAnalyzer appServiceAnalyzer,
        AnalysisStorageService storageService,
        ILogger<CostAnalysisOrchestrator> logger)
    {
        _diskAnalyzer = diskAnalyzer;
        _storageAnalyzer = storageAnalyzer;
        _publicIpAnalyzer = publicIpAnalyzer;
        _idleVmAnalyzer = idleVmAnalyzer;
        _appServiceAnalyzer = appServiceAnalyzer;
        _storageService = storageService;
        _logger = logger;
    }

    /// <summary>
    /// Executa análise completa baseada na requisição
    /// </summary>
    public async Task<CostAnalysisResult> ExecuteAnalysisAsync(CostAnalysisRequest request)
    {
        // 🐛 DEBUG: Log completo da requisição
        Console.WriteLine($"🐛 ExecuteAnalysisAsync - Request recebido:");
        Console.WriteLine($"🐛   Request: {(request == null ? "NULL" : "NOT NULL")}");
        if (request != null)
        {
            Console.WriteLine($"🐛   Scope: '{request.Scope ?? "NULL"}'");
            Console.WriteLine($"🐛   SubscriptionId: '{request.SubscriptionId ?? "NULL"}'");
            Console.WriteLine($"🐛   DryRun: {request.DryRun}");
            Console.WriteLine($"🐛   AnalysisOptions: {(request.AnalysisOptions == null ? "NULL" : "NOT NULL")}");
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

            // 🚀 NÍVEL 4: Múltiplos analyzers executando em paralelo
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

            // 🖥️ VMs Idle - Maior impacto FinOps
            if (request.AnalysisOptions.IdleVms)
            {
                tasks.Add(AnalyzeIdleVmsAsync(request));
            }

            // 🌐 App Services - Análise de utilização
            if (request.AnalysisOptions.AppServices)
            {
                tasks.Add(AnalyzeAppServicesAsync(request));
            }
            
            // Executar todas as análises em paralelo
            var results = await Task.WhenAll(tasks);
            foreach (var recommendations in results)
            {
                allRecommendations.AddRange(recommendations);
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
            Console.WriteLine("💾 Iniciando análise de discos não anexados...");
            
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

            Console.WriteLine("⚠️ Discos: Escopo não suportado ou SubscriptionId não fornecido");
            return new List<CostRecommendation>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Erro ao analisar discos não anexados: {ex.Message}");
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

        // 🔥 BLINDAGEM FINAL: AnalysisOptions nunca null
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
    /// 🏪 NÍVEL 4: Análise de Storage Accounts subutilizados
    /// </summary>
    private async Task<List<CostRecommendation>> AnalyzeStorageAccountsAsync(CostAnalysisRequest request)
    {
        try
        {
            Console.WriteLine("🏪 Iniciando análise de Storage Accounts...");
            
            if (request.Scope == "subscription" && !string.IsNullOrEmpty(request.SubscriptionId))
            {
                var standardResult = await _storageAnalyzer.AnalyzeSubscriptionAsync(
                    request.SubscriptionId, 
                    request.AnalysisPeriodDays, 
                    request.DryRun);

                // Converter StandardFinding para CostRecommendation (compatibilidade)
                return ConvertToLegacyFormat(standardResult.Findings);
            }

            Console.WriteLine("⚠️ Storage: Escopo não suportado ou SubscriptionId não fornecido");
            return new List<CostRecommendation>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Erro na análise de Storage Accounts: {ex.Message}");
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
            Console.WriteLine("🌐 Iniciando análise de Public IPs ociosos...");
            
            if (request.Scope == "subscription" && !string.IsNullOrEmpty(request.SubscriptionId))
            {
                var standardResult = await _publicIpAnalyzer.AnalyzeAsync(
                    request.SubscriptionId, 
                    request.AnalysisPeriodDays, 
                    request.DryRun);

                // Converter StandardFinding para CostRecommendation (compatibilidade)
                return ConvertToLegacyFormat(standardResult.Findings);
            }

            Console.WriteLine("⚠️ Public IPs: Escopo não suportado ou SubscriptionId não fornecido");
            return new List<CostRecommendation>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Erro na análise de Public IPs: {ex.Message}");
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
            Console.WriteLine("🖥️ Iniciando análise de VMs ociosas - maior impacto FinOps...");
            
            if (request.Scope == "subscription" && !string.IsNullOrEmpty(request.SubscriptionId))
            {
                var standardResult = await _idleVmAnalyzer.AnalyzeAsync(
                    request.SubscriptionId, 
                    request.AnalysisPeriodDays, 
                    request.DryRun);

                // Converter StandardFinding para CostRecommendation (compatibilidade)
                return ConvertToLegacyFormat(standardResult.Findings);
            }

            Console.WriteLine("⚠️ VMs Idle: Escopo não suportado ou SubscriptionId não fornecido");
            return new List<CostRecommendation>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Erro na análise de VMs ociosas: {ex.Message}");
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
            Console.WriteLine("🌐 Iniciando análise de App Services ociosos...");
            
            if (request.Scope == "subscription" && !string.IsNullOrEmpty(request.SubscriptionId))
            {
                var standardResult = await _appServiceAnalyzer.AnalyzeAsync(
                    request.SubscriptionId, 
                    request.AnalysisPeriodDays, 
                    request.DryRun);

                // Converter StandardFinding para CostRecommendation (compatibilidade)
                return ConvertToLegacyFormat(standardResult.Findings);
            }

            Console.WriteLine("⚠️ App Services: Escopo não suportado ou SubscriptionId não fornecido");
            return new List<CostRecommendation>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Erro na análise de App Services: {ex.Message}");
            return new List<CostRecommendation>();
        }
    }

    /// <summary>
    /// ⚡ Conversão do StandardFinding (novo) para CostRecommendation (legacy)
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
    /// 🏆 NOVO: Constrói Top 10 automático das maiores economias do dia
    /// Pipeline: ler recommendations.json → normalizar → ordenar → salvar top10.json
    /// </summary>
    public async Task<DailyTop10Result?> BuildTop10Async(DateTime analysisDate)
    {
        try
        {
            _logger.LogInformation("🏆 Iniciando construção do Top 10 para {date}", analysisDate.ToString("yyyy-MM-dd"));

            // 1️⃣ Ler todos os recommendations.json do dia
            var allRecommendations = await _storageService.GetDailyAnalysisAsync(analysisDate);
            
            if (!allRecommendations.Any())
            {
                _logger.LogWarning("📄 Nenhuma recomendação encontrada para {date}", analysisDate.ToString("yyyy-MM-dd"));
                return null;
            }

            // 2️⃣ Normalizar para TopSavingCandidate
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

            // 3️⃣ Ordenar por economia e pegar Top 10
            var top10 = candidates
                .OrderByDescending(x => x.EstimatedMonthlySavings)
                .Take(10)
                .Select((candidate, index) => 
                {
                    candidate.Rank = index + 1;
                    return candidate;
                })
                .ToList();

            // 4️⃣ Calcular estatísticas
            var uniqueSubscriptions = candidates.Select(c => c.SubscriptionId).Distinct().Count();
            var totalSavings = candidates.Sum(c => c.EstimatedMonthlySavings);

            var result = new DailyTop10Result
            {
                Date = analysisDate.ToString("yyyy-MM-dd"),
                TotalSubscriptions = uniqueSubscriptions,
                TotalSavings = totalSavings,
                Top10 = top10
            };

            _logger.LogInformation("🏆 Top 10 construído: {total} recomendações, R$ {savings} economia total", 
                candidates.Count, totalSavings);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao construir Top 10 para {date}", analysisDate.ToString("yyyy-MM-dd"));
            return null;
        }
    }

    /// <summary>
    /// 🟢 EXECUÇÃO DIRETA: Análises DIÁRIAS (rápidas, sem métricas pesadas)
    /// 🎯 PROCESSAMENTO DIRETO: Executado pelo Timer Function (SIMULAÇÃO)
    /// </summary>
    public async Task RunDailyAnalysisAsync(string subscriptionId)
    {
        _logger.LogInformation("🟢 SIMULAÇÃO: Executando análises DIÁRIAS para subscription {subscriptionId}", subscriptionId);
        
        await Task.Delay(1000); // Simula processamento
        
        _logger.LogInformation("✅ SIMULAÇÃO: Análises DIÁRIAS concluídas para {subscriptionId} - Discos órfãos: 5, IPs órfãos: 2", subscriptionId);
    }

    /// <summary>
    /// 🟡 EXECUÇÃO DIRETA: Análises 2X SEMANA (pesadas, com Azure Monitor)
    /// 🎯 PROCESSAMENTO DIRETO: Com timeout e circuit breaker (SIMULAÇÃO)
    /// </summary>
    public async Task RunBiWeeklyAnalysisAsync(string subscriptionId)
    {
        _logger.LogInformation("🟡 SIMULAÇÃO: Executando análises 2X SEMANA para subscription {subscriptionId}", subscriptionId);
        
        await Task.Delay(2000); // Simula processamento mais longo
        
        _logger.LogInformation("✅ SIMULAÇÃO: Análises 2X SEMANA concluídas para {subscriptionId} - VMs idle: 3, Storage accounts: 7, App services: 1", subscriptionId);
    }
}
