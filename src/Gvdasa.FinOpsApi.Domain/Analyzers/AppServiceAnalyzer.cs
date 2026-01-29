using Gvdasa.FinOpsApi.Modelos.FinOps;

namespace Gvdasa.GVmodeloexemploapi.Domain.Analyzers;

public class AppServiceAnalyzer : BaseAnalyzer
{
    public override string ResourceType => "Microsoft.Web/sites";
    
    private const int LOW_REQUEST_THRESHOLD = 100; // requests por dia
    private const double LOW_CPU_THRESHOLD = 10.0;
    private const decimal MINIMUM_COST_TO_ANALYZE = 80m;

    public AppServiceAnalyzer(ILogger<AppServiceAnalyzer> logger) : base(logger) { }

    public override async Task<IEnumerable<OptimizationFinding>> AnalyzeAsync(CostRecord costRecord, ResourceUsage? usage)
    {
        var findings = new List<OptimizationFinding>();
        
        try
        {
            _logger.LogInformation("Analisando App Service {ResourceName} com custo mensal de {Cost:C}", 
                costRecord.ResourceName, costRecord.MonthlyCost);

            if (costRecord.MonthlyCost < MINIMUM_COST_TO_ANALYZE)
            {
                return findings;
            }

            if (usage != null)
            {
                // Análise 1: App Service com baixo tráfego
                if (usage.HttpRequests <= LOW_REQUEST_THRESHOLD)
                {
                    findings.Add(CreateLowTrafficAppServiceFinding(costRecord, usage));
                }

                // Análise 2: App Service superdimensionado (CPU baixa)
                if (usage.CpuPercentage <= LOW_CPU_THRESHOLD && usage.HttpRequests > LOW_REQUEST_THRESHOLD)
                {
                    findings.Add(CreateOversizedAppServiceFinding(costRecord, usage));
                }

                // Análise 3: Oportunidade de Reserved Instance para App Service Plans caros
                if (costRecord.MonthlyCost >= 200m && usage.IsRunning)
                {
                    findings.Add(CreateAppServiceReservedInstanceFinding(costRecord, usage));
                }

                // Análise 4: Considerar Azure Functions para workloads esporádicos
                if (usage.HttpRequests > 0 && usage.HttpRequests <= 1000 && usage.CpuPercentage <= 5.0)
                {
                    findings.Add(CreateFunctionAppOpportunityFinding(costRecord, usage));
                }
            }

            return findings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao analisar App Service {ResourceName}", costRecord.ResourceName);
            return findings;
        }
    }

    private OptimizationFinding CreateLowTrafficAppServiceFinding(CostRecord costRecord, ResourceUsage usage)
    {
        var estimatedSaving = costRecord.MonthlyCost * 0.7m; // 70% economia com plano menor ou desligando
        
        var evidence = new Dictionary<string, object>
        {
            ["dailyHttpRequests"] = usage.HttpRequests,
            ["avgCpuPercentage"] = usage.CpuPercentage,
            ["avgResponseTime"] = usage.ResponseTime,
            ["requestsThreshold"] = LOW_REQUEST_THRESHOLD
        };

        return CreateFinding(
            costRecord,
            OptimizationType.APP_SERVICE_IDLE,
            "App Service com pouco tráfego",
            $"App Service {costRecord.ResourceName} recebe apenas {usage.HttpRequests} requests/dia, muito abaixo do esperado para o plano atual ({costRecord.MonthlyCost:C}/mês).",
            "Considerar: 1) Migrar para plano Shared ou Basic, 2) Combinar com outras apps no mesmo plano, 3) Usar Azure Functions se for workload esporádico.",
            estimatedSaving,
            evidence
        );
    }

    private OptimizationFinding CreateOversizedAppServiceFinding(CostRecord costRecord, ResourceUsage usage)
    {
        var estimatedSaving = costRecord.MonthlyCost * 0.4m; // 40% economia com plano menor
        
        var evidence = new Dictionary<string, object>
        {
            ["dailyHttpRequests"] = usage.HttpRequests,
            ["avgCpuPercentage"] = usage.CpuPercentage,
            ["avgResponseTime"] = usage.ResponseTime,
            ["currentPlan"] = "Inferido como Standard ou Premium baseado no custo"
        };

        return CreateFinding(
            costRecord,
            OptimizationType.APP_SERVICE_IDLE,
            "App Service superdimensionado",
            $"App Service {costRecord.ResourceName} tem bom volume de requests ({usage.HttpRequests}/dia) mas CPU baixa ({usage.CpuPercentage:F1}%). Plano pode ser reduzido.",
            "Reduzir o Service Plan para um tier menor (ex: de Standard S2 para S1, ou de Premium P2 para P1). Monitorar performance após a mudança.",
            estimatedSaving,
            evidence
        );
    }

    private OptimizationFinding CreateAppServiceReservedInstanceFinding(CostRecord costRecord, ResourceUsage usage)
    {
        var estimatedSaving = costRecord.MonthlyCost * 0.3m; // 30% economia com RI
        
        var evidence = new Dictionary<string, object>
        {
            ["currentMonthlyCost"] = costRecord.MonthlyCost,
            ["estimatedRISaving"] = "30% com Reserved Instance",
            ["isStableWorkload"] = usage.HttpRequests > 0 && usage.IsRunning
        };

        return CreateFinding(
            costRecord,
            OptimizationType.RESERVED_INSTANCE_OPPORTUNITY,
            "Oportunidade Reserved Instance - App Service",
            $"App Service Plan de {costRecord.ResourceName} com custo alto ({costRecord.MonthlyCost:C}/mês) pode se beneficiar de Reserved Instance.",
            "Avaliar compra de Reserved Instance para App Service Plan, especialmente se for um workload estável que roda 24x7.",
            estimatedSaving,
            evidence
        );
    }

    private OptimizationFinding CreateFunctionAppOpportunityFinding(CostRecord costRecord, ResourceUsage usage)
    {
        var estimatedSaving = costRecord.MonthlyCost * 0.8m; // 80% economia com Functions (pay-per-use)
        
        var evidence = new Dictionary<string, object>
        {
            ["dailyHttpRequests"] = usage.HttpRequests,
            ["avgCpuPercentage"] = usage.CpuPercentage,
            ["workloadPattern"] = "Low frequency, event-driven candidate"
        };

        return CreateFinding(
            costRecord,
            OptimizationType.APP_SERVICE_IDLE,
            "Candidato para Azure Functions",
            $"App Service {costRecord.ResourceName} tem padrão de uso esporádico ({usage.HttpRequests} requests/dia, CPU {usage.CpuPercentage:F1}%). Azure Functions pode ser mais econômico.",
            "Avaliar migração para Azure Functions (Consumption Plan) se a aplicação for event-driven ou tiver uso esporádico. Pague apenas pelo que usar.",
            estimatedSaving,
            evidence
        );
    }
}