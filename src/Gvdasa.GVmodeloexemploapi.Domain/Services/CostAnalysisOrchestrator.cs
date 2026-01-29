using Gvdasa.GVmodeloexemploapi.Domain.Analyzers;
using Gvdasa.GVmodeloexemploapi.Domain.Configuration;
using Gvdasa.GVmodeloexemploapi.Infra.Services.FinOps;
using Gvdasa.GVmodeloexemploapi.Modelos.FinOps;
using Microsoft.Extensions.Options;

namespace Gvdasa.GVmodeloexemploapi.Domain.Services;

public interface ICostAnalysisOrchestrator
{
    Task<CostAnalysisResult> AnalyzeSubscriptionAsync(string subscriptionId, int analysisPeriodDays = 30);
    Task<CostAnalysisResult> AnalyzeAllSubscriptionsAsync(int analysisPeriodDays = 30);
    Task<CostAnalysisResult> AnalyzeResourceGroupAsync(string subscriptionId, string resourceGroupName, int analysisPeriodDays = 30);
}

public class CostAnalysisOrchestrator : ICostAnalysisOrchestrator
{
    private readonly ICostManagementService _costService;
    private readonly IMetricsService _metricsService;
    private readonly IResourceGraphService _resourceGraphService;
    private readonly IEnumerable<IResourceAnalyzer> _analyzers;
    private readonly AnalyzerOptions _options;
    private readonly ILogger<CostAnalysisOrchestrator> _logger;

    public CostAnalysisOrchestrator(
        ICostManagementService costService,
        IMetricsService metricsService,
        IResourceGraphService resourceGraphService,
        IEnumerable<IResourceAnalyzer> analyzers,
        IOptions<AnalyzerOptions> options,
        ILogger<CostAnalysisOrchestrator> logger)
    {
        _costService = costService;
        _metricsService = metricsService;
        _resourceGraphService = resourceGraphService;
        _analyzers = analyzers;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<CostAnalysisResult> AnalyzeSubscriptionAsync(string subscriptionId, int analysisPeriodDays = 30)
    {
        try
        {
            _logger.LogInformation("Iniciando análise de custo para subscription {SubscriptionId}", subscriptionId);
            
            var endDate = DateTime.UtcNow.Date;
            var startDate = endDate.AddDays(-analysisPeriodDays);
            
            // 1. Buscar dados de custo
            var costRecords = await _costService.GetCostsBySubscriptionAsync(subscriptionId, startDate, endDate);
            
            // 2. Executar análise completa
            var result = await ExecuteAnalysisAsync(costRecords.ToList(), subscriptionId);
            result.AnalysisPeriodDays = analysisPeriodDays;
            result.AnalysisScope = $"Subscription: {subscriptionId}";
            
            _logger.LogInformation("Análise concluída para subscription {SubscriptionId}. {FindingCount} achados, economia potencial: {TotalSaving:C}", 
                subscriptionId, result.TotalFindings, result.TotalPotentialSaving);
                
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro durante análise de subscription {SubscriptionId}", subscriptionId);
            throw;
        }
    }

    public async Task<CostAnalysisResult> AnalyzeAllSubscriptionsAsync(int analysisPeriodDays = 30)
    {
        try
        {
            _logger.LogInformation("Iniciando análise de custo para todas as subscriptions");
            
            var endDate = DateTime.UtcNow.Date;
            var startDate = endDate.AddDays(-analysisPeriodDays);
            
            // 1. Buscar dados de custo para todas as subscriptions
            var costRecords = await _costService.GetCostsForAllSubscriptionsAsync(startDate, endDate, _options.Scope.SubscriptionIds);
            
            // 2. Executar análise completa
            var result = await ExecuteAnalysisAsync(costRecords.ToList(), "all-subscriptions");
            result.AnalysisPeriodDays = analysisPeriodDays;
            result.AnalysisScope = "All Subscriptions";
            
            _logger.LogInformation("Análise concluída para todas as subscriptions. {FindingCount} achados, economia potencial: {TotalSaving:C}", 
                result.TotalFindings, result.TotalPotentialSaving);
                
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro durante análise de todas as subscriptions");
            throw;
        }
    }

    public async Task<CostAnalysisResult> AnalyzeResourceGroupAsync(string subscriptionId, string resourceGroupName, int analysisPeriodDays = 30)
    {
        try
        {
            _logger.LogInformation("Iniciando análise de custo para resource group {ResourceGroup} na subscription {SubscriptionId}", 
                resourceGroupName, subscriptionId);
            
            var endDate = DateTime.UtcNow.Date;
            var startDate = endDate.AddDays(-analysisPeriodDays);
            
            var costRecords = await _costService.GetCostsByResourceGroupAsync(subscriptionId, resourceGroupName, startDate, endDate);
            
            var result = await ExecuteAnalysisAsync(costRecords.ToList(), subscriptionId);
            result.AnalysisPeriodDays = analysisPeriodDays;
            result.AnalysisScope = $"Resource Group: {resourceGroupName}";
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro durante análise de resource group {ResourceGroup}", resourceGroupName);
            throw;
        }
    }

    private async Task<CostAnalysisResult> ExecuteAnalysisAsync(List<CostRecord> costRecords, string scope)
    {
        var result = new CostAnalysisResult
        {
            AnalysisId = Guid.NewGuid(),
            AnalysisDate = DateTime.UtcNow,
            AnalysisScope = scope
        };

        try
        {
            _logger.LogInformation("Executando análise para {RecordCount} registros de custo", costRecords.Count);
            
            // Filtrar registros que atendem ao custo mínimo
            var significantCosts = costRecords.Where(c => c.MonthlyCost >= _options.MinimumCostToAnalyze).ToList();
            _logger.LogInformation("Filtrados {SignificantCount} registros com custo >= {MinCost:C}", 
                significantCosts.Count, _options.MinimumCostToAnalyze);

            // Buscar métricas de uso em paralelo
            var resourceIds = significantCosts.Select(c => c.ResourceId).ToList();
            var usageData = await GetUsageDataAsync(resourceIds);

            // Executar análise por tipo de recurso
            var allFindings = new List<OptimizationFinding>();
            
            foreach (var analyzer in _analyzers)
            {
                if (!IsAnalyzerEnabled(analyzer.ResourceType))
                {
                    _logger.LogInformation("Analyzer {ResourceType} desabilitado, pulando", analyzer.ResourceType);
                    continue;
                }

                var relevantCosts = significantCosts.Where(c => 
                    c.ResourceType.Equals(analyzer.ResourceType, StringComparison.OrdinalIgnoreCase)).ToList();
                
                if (!relevantCosts.Any())
                {
                    continue;
                }

                _logger.LogInformation("Executando {AnalyzerType} para {ResourceCount} recursos", 
                    analyzer.GetType().Name, relevantCosts.Count);

                var findings = await ExecuteAnalyzerAsync(analyzer, relevantCosts, usageData);
                allFindings.AddRange(findings);
                
                _logger.LogInformation("{AnalyzerType} concluído. {FindingCount} achados gerados", 
                    analyzer.GetType().Name, findings.Count);
            }

            // Consolidar resultados
            result.Findings = allFindings.OrderByDescending(f => f.EstimatedMonthlySaving).ToList();
            result.TotalFindings = allFindings.Count;
            result.TotalPotentialSaving = allFindings.Sum(f => f.EstimatedMonthlySaving);
            result.TotalAnalyzedResources = significantCosts.Count;
            result.TotalMonthlyCost = costRecords.Sum(c => c.MonthlyCost);

            // Estatísticas por severidade
            result.FindingsBySeverity = allFindings
                .GroupBy(f => f.Severity)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());

            // Top 10 oportunidades de economia
            result.TopSavingOpportunities = allFindings
                .OrderByDescending(f => f.EstimatedMonthlySaving)
                .Take(10)
                .ToList();

            _logger.LogInformation("Análise concluída com sucesso. Total de achados: {FindingCount}, Economia potencial: {TotalSaving:C}", 
                result.TotalFindings, result.TotalPotentialSaving);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro durante execução da análise");
            result.ErrorMessage = ex.Message;
            throw;
        }
    }

    private async Task<Dictionary<string, ResourceUsage?>> GetUsageDataAsync(List<string> resourceIds)
    {
        try
        {
            _logger.LogInformation("Coletando dados de uso para {ResourceCount} recursos", resourceIds.Count);
            
            var endDate = DateTime.UtcNow.AddDays(-1); // Dados até ontem
            var startDate = endDate.AddDays(-7); // Últimos 7 dias
            
            var usageData = await _metricsService.GetResourceUsageForMultipleResourcesAsync(resourceIds, startDate, endDate);
            
            var usageDictionary = usageData.ToDictionary(u => u.ResourceId, u => (ResourceUsage?)u);
            
            _logger.LogInformation("Coletados dados de uso para {UsageCount} recursos", usageDictionary.Count);
            
            return usageDictionary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao coletar dados de uso. Continuando análise sem métricas");
            return new Dictionary<string, ResourceUsage?>();
        }
    }

    private async Task<List<OptimizationFinding>> ExecuteAnalyzerAsync(
        IResourceAnalyzer analyzer, 
        List<CostRecord> costRecords, 
        Dictionary<string, ResourceUsage?> usageData)
    {
        var findings = new List<OptimizationFinding>();
        
        foreach (var costRecord in costRecords)
        {
            try
            {
                usageData.TryGetValue(costRecord.ResourceId, out var usage);
                var resourceFindings = await analyzer.AnalyzeAsync(costRecord, usage);
                findings.AddRange(resourceFindings);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao analisar recurso {ResourceId} com {AnalyzerType}", 
                    costRecord.ResourceId, analyzer.GetType().Name);
            }
        }
        
        return findings;
    }

    private bool IsAnalyzerEnabled(string resourceType)
    {
        return resourceType.ToLower() switch
        {
            "microsoft.compute/virtualmachines" => _options.EnableVmAnalysis,
            "microsoft.compute/disks" => _options.EnableDiskAnalysis,
            "microsoft.web/sites" => _options.EnableAppServiceAnalysis,
            "microsoft.sql/servers/databases" => _options.EnableSqlAnalysis,
            _ => true // Por padrão, habilita analyzers não mapeados
        };
    }
}

public class CostAnalysisResult
{
    public Guid AnalysisId { get; set; }
    public DateTime AnalysisDate { get; set; }
    public string AnalysisScope { get; set; } = string.Empty;
    public int AnalysisPeriodDays { get; set; }
    
    public List<OptimizationFinding> Findings { get; set; } = new();
    public int TotalFindings { get; set; }
    public decimal TotalPotentialSaving { get; set; }
    public decimal TotalMonthlyCost { get; set; }
    public int TotalAnalyzedResources { get; set; }
    
    public Dictionary<string, int> FindingsBySeverity { get; set; } = new();
    public List<OptimizationFinding> TopSavingOpportunities { get; set; } = new();
    
    public string? ErrorMessage { get; set; }
}