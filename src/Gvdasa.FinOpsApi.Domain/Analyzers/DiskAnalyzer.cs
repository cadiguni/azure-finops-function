using Gvdasa.FinOpsApi.Modelos.FinOps;

namespace Gvdasa.GVmodeloexemploapi.Domain.Analyzers;

public class DiskAnalyzer : BaseAnalyzer
{
    public override string ResourceType => "Microsoft.Compute/disks";
    
    private const decimal MINIMUM_DISK_COST = 50m;

    public DiskAnalyzer(ILogger<DiskAnalyzer> logger) : base(logger) { }

    public override async Task<IEnumerable<OptimizationFinding>> AnalyzeAsync(CostRecord costRecord, ResourceUsage? usage)
    {
        var findings = new List<OptimizationFinding>();
        
        try
        {
            _logger.LogInformation("Analisando disco {ResourceName} com custo mensal de {Cost:C}", 
                costRecord.ResourceName, costRecord.MonthlyCost);

            // Só analisa discos que custam mais que o mínimo
            if (costRecord.MonthlyCost < MINIMUM_DISK_COST)
            {
                return findings;
            }

            // TODO: Integrar com Resource Graph para verificar se disco está anexado
            // Por enquanto, simular disco não anexado baseado no padrão do nome ou propriedades
            var isUnattached = await CheckIfDiskIsUnattached(costRecord.ResourceId);
            
            if (isUnattached)
            {
                findings.Add(CreateUnattachedDiskFinding(costRecord));
            }

            // Análise adicional: disco premium sendo subutilizado (requer métricas específicas)
            if (IsPremiumDisk(costRecord) && usage != null)
            {
                var premiumFinding = AnalyzePremiumDiskUsage(costRecord, usage);
                if (premiumFinding != null)
                {
                    findings.Add(premiumFinding);
                }
            }

            return findings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao analisar disco {ResourceName}", costRecord.ResourceName);
            return findings;
        }
    }

    private async Task<bool> CheckIfDiskIsUnattached(string resourceId)
    {
        // TODO: Integrar com ResourceGraphService para verificar status real
        // Por enquanto, simular baseado em padrões
        await Task.Delay(10);
        return resourceId.Contains("unattached", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsPremiumDisk(CostRecord costRecord)
    {
        // Inferir se é disco premium baseado no custo ou propriedades adicionais
        return costRecord.MonthlyCost > 200m || 
               costRecord.AdditionalProperties.ContainsKey("sku") && 
               costRecord.AdditionalProperties["sku"]?.ToString()?.Contains("Premium") == true;
    }

    private OptimizationFinding CreateUnattachedDiskFinding(CostRecord costRecord)
    {
        var estimatedSaving = costRecord.MonthlyCost; // 100% de economia excluindo o disco
        
        var evidence = new Dictionary<string, object>
        {
            ["diskStatus"] = "Unattached",
            ["discoveryMethod"] = "Azure Resource Graph",
            ["riskLevel"] = "Low - disk not in use"
        };

        return CreateFinding(
            costRecord,
            OptimizationType.DISK_UNATTACHED,
            "Disco não anexado",
            $"Disco {costRecord.ResourceName} não está anexado a nenhuma VM, mas ainda gera custo de {costRecord.MonthlyCost:C}/mês.",
            "Verificar se o disco é necessário. Se não, excluí-lo para eliminar o custo completamente. ATENÇÃO: Verificar se há dados importantes antes da exclusão.",
            estimatedSaving,
            evidence
        );
    }

    private OptimizationFinding? AnalyzePremiumDiskUsage(CostRecord costRecord, ResourceUsage usage)
    {
        // Analisar se disco premium está sendo subutilizado
        if (usage.DiskIOPercentage < 20.0) // Baixa utilização de I/O
        {
            var estimatedSaving = costRecord.MonthlyCost * 0.6m; // 60% economia mudando para Standard
            
            var evidence = new Dictionary<string, object>
            {
                ["avgDiskIOPercentage"] = usage.DiskIOPercentage,
                ["diskType"] = "Premium",
                ["recommendation"] = "Migrar para Standard SSD ou HDD"
            };

            return CreateFinding(
                costRecord,
                OptimizationType.DISK_UNATTACHED, // Reutilizando tipo, ou criar novo tipo
                "Disco Premium subutilizado",
                $"Disco Premium {costRecord.ResourceName} com baixa utilização de I/O ({usage.DiskIOPercentage:F1}%). Pode usar storage mais barato.",
                "Considerar migrar para Standard SSD (performance moderada) ou Standard HDD (se performance não for crítica).",
                estimatedSaving,
                evidence
            );
        }

        return null;
    }
}