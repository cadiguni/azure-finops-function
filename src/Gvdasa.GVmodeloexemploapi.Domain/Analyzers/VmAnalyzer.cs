using Gvdasa.GVmodeloexemploapi.Modelos.FinOps;

namespace Gvdasa.GVmodeloexemploapi.Domain.Analyzers;

public class VmAnalyzer : BaseAnalyzer
{
    public override string ResourceType => "Microsoft.Compute/virtualMachines";
    
    // Configurações (podem vir de appsettings)
    private const double LOW_CPU_THRESHOLD = 5.0;
    private const double VERY_LOW_CPU_THRESHOLD = 2.0;
    private const decimal MINIMUM_COST_TO_ANALYZE = 100m;
    private const int INACTIVE_DAYS_THRESHOLD = 7;

    public VmAnalyzer(ILogger<VmAnalyzer> logger) : base(logger) { }

    public override async Task<IEnumerable<OptimizationFinding>> AnalyzeAsync(CostRecord costRecord, ResourceUsage? usage)
    {
        var findings = new List<OptimizationFinding>();
        
        try
        {
            _logger.LogInformation("Analisando VM {ResourceName} com custo mensal de {Cost:C}", 
                costRecord.ResourceName, costRecord.MonthlyCost);

            // Só analisa VMs que custam mais que o mínimo
            if (costRecord.MonthlyCost < MINIMUM_COST_TO_ANALYZE)
            {
                return findings;
            }

            // Análise 1: VM Idle (CPU muito baixa)
            if (usage != null && usage.CpuPercentage <= VERY_LOW_CPU_THRESHOLD)
            {
                findings.Add(CreateIdleVmFinding(costRecord, usage));
            }
            // Análise 2: VM com baixa utilização (pode ser redimensionada)
            else if (usage != null && usage.CpuPercentage <= LOW_CPU_THRESHOLD && usage.CpuPercentage > VERY_LOW_CPU_THRESHOLD)
            {
                findings.Add(CreateOversizedVmFinding(costRecord, usage));
            }

            // Análise 3: VM desligada por muitos dias
            if (usage != null && usage.DaysInactive >= INACTIVE_DAYS_THRESHOLD)
            {
                findings.Add(CreateInactiveVmFinding(costRecord, usage));
            }

            // Análise 4: Oportunidade de Reserved Instance (para VMs estáveis)
            if (usage != null && usage.IsRunning && costRecord.MonthlyCost >= 300m)
            {
                findings.Add(CreateReservedInstanceOpportunityFinding(costRecord, usage));
            }

            _logger.LogInformation("VM {ResourceName} analisada. {FindingCount} achados gerados", 
                costRecord.ResourceName, findings.Count);

            return findings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao analisar VM {ResourceName}", costRecord.ResourceName);
            return findings;
        }
    }

    private OptimizationFinding CreateIdleVmFinding(CostRecord costRecord, ResourceUsage usage)
    {
        var estimatedSaving = costRecord.MonthlyCost * 0.85m; // 85% de economia desligando
        
        var evidence = new Dictionary<string, object>
        {
            ["avgCpuPercentage"] = usage.CpuPercentage,
            ["avgMemoryPercentage"] = usage.MemoryPercentage,
            ["measurementDate"] = usage.MeasurementDate,
            ["analysisThreshold"] = VERY_LOW_CPU_THRESHOLD
        };

        return CreateFinding(
            costRecord,
            OptimizationType.VM_IDLE,
            "VM com utilização muito baixa",
            $"VM {costRecord.ResourceName} está com CPU média de {usage.CpuPercentage:F1}%, muito abaixo do esperado para uma VM ativa. Custos: {costRecord.MonthlyCost:C}/mês.",
            "Considere desligar a VM temporariamente ou permanentemente se não for necessária. Alternativa: redimensionar para SKU menor.",
            estimatedSaving,
            evidence
        );
    }

    private OptimizationFinding CreateOversizedVmFinding(CostRecord costRecord, ResourceUsage usage)
    {
        var estimatedSaving = costRecord.MonthlyCost * 0.4m; // 40% de economia redimensionando
        
        var evidence = new Dictionary<string, object>
        {
            ["avgCpuPercentage"] = usage.CpuPercentage,
            ["avgMemoryPercentage"] = usage.MemoryPercentage,
            ["recommendation"] = "Reduzir SKU da VM",
            ["analysisThreshold"] = LOW_CPU_THRESHOLD
        };

        return CreateFinding(
            costRecord,
            OptimizationType.VM_OVERSIZED,
            "VM superdimensionada",
            $"VM {costRecord.ResourceName} está com utilização baixa (CPU {usage.CpuPercentage:F1}%). Pode ser redimensionada para economizar custos.",
            "Redimensionar para um SKU menor (ex: de Standard_D4s_v3 para Standard_D2s_v3). Teste em horário de menor uso.",
            estimatedSaving,
            evidence
        );
    }

    private OptimizationFinding CreateInactiveVmFinding(CostRecord costRecord, ResourceUsage usage)
    {
        var estimatedSaving = costRecord.MonthlyCost * 0.9m; // 90% de economia
        
        var evidence = new Dictionary<string, object>
        {
            ["daysInactive"] = usage.DaysInactive,
            ["lastStopTime"] = usage.LastStopTime,
            ["isRunning"] = usage.IsRunning
        };

        return CreateFinding(
            costRecord,
            OptimizationType.VM_IDLE,
            "VM inativa há muito tempo",
            $"VM {costRecord.ResourceName} está desligada há {usage.DaysInactive} dias, mas ainda gerando custos de storage.",
            "Avaliar se a VM ainda é necessária. Se não, considere excluí-la para eliminar custos de disco e outros recursos associados.",
            estimatedSaving,
            evidence
        );
    }

    private OptimizationFinding CreateReservedInstanceOpportunityFinding(CostRecord costRecord, ResourceUsage usage)
    {
        var estimatedSaving = costRecord.MonthlyCost * 0.25m; // 25% economia com RI de 1 ano
        
        var evidence = new Dictionary<string, object>
        {
            ["currentMonthlyCost"] = costRecord.MonthlyCost,
            ["estimatedRISaving"] = "25% com Reserved Instance de 1 ano, 40% com 3 anos",
            ["isStableWorkload"] = usage.IsRunning && usage.CpuPercentage > 0
        };

        return CreateFinding(
            costRecord,
            OptimizationType.RESERVED_INSTANCE_OPPORTUNITY,
            "Oportunidade de Reserved Instance",
            $"VM {costRecord.ResourceName} com custo alto ({costRecord.MonthlyCost:C}/mês) e uso estável pode se beneficiar de Reserved Instance.",
            "Avaliar compra de Reserved Instance de 1 ou 3 anos para esta VM, considerando o padrão de uso estável.",
            estimatedSaving,
            evidence
        );
    }
}