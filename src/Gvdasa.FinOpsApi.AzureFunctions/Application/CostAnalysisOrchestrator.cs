using Gvdasa.FinOpsApi.AzureFunctions.Analyzers;
using Gvdasa.FinOpsApi.AzureFunctions.Models;

namespace Gvdasa.FinOpsApi.AzureFunctions.Application;

public class CostAnalysisOrchestrator
{
    private readonly UnattachedDiskAnalyzer _diskAnalyzer;
    private readonly StorageAccountAnalyzer _storageAnalyzer;
    private readonly UnusedPublicIpAnalyzer _publicIpAnalyzer;

    public CostAnalysisOrchestrator(
        UnattachedDiskAnalyzer diskAnalyzer, 
        StorageAccountAnalyzer storageAnalyzer,
        UnusedPublicIpAnalyzer publicIpAnalyzer)
    {
        _diskAnalyzer = diskAnalyzer;
        _storageAnalyzer = storageAnalyzer;
        _publicIpAnalyzer = publicIpAnalyzer;
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
        var recommendations = new List<CostRecommendation>();

        try
        {
            if (request.Scope.Equals("subscription", StringComparison.OrdinalIgnoreCase) && 
                !string.IsNullOrEmpty(request.SubscriptionId))
            {
                var diskRecs = await _diskAnalyzer.AnalyzeSubscriptionAsync(request.SubscriptionId);
                recommendations.AddRange(diskRecs);
            }
            else if (request.Scope.Equals("managementGroup", StringComparison.OrdinalIgnoreCase))
            {
                // TODO: Implementar análise por Management Group
                // Requer listagem de subscriptions no MG
                Console.WriteLine("Análise por Management Group ainda não implementada");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro ao analisar discos não anexados: {ex.Message}");
        }

        return recommendations;
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
                return await _storageAnalyzer.AnalyzeSubscriptionAsync(request.SubscriptionId);
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
                return await _publicIpAnalyzer.AnalyzeAsync(request.SubscriptionId);
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
}