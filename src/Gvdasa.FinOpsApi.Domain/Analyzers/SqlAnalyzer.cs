using Gvdasa.FinOpsApi.Modelos.FinOps;

namespace Gvdasa.GVmodeloexemploapi.Domain.Analyzers;

public class SqlAnalyzer : BaseAnalyzer
{
    public override string ResourceType => "Microsoft.Sql/servers/databases";
    
    private const double LOW_DTU_THRESHOLD = 20.0;
    private const double LOW_STORAGE_THRESHOLD = 50.0;
    private const decimal MINIMUM_COST_TO_ANALYZE = 100m;

    public SqlAnalyzer(ILogger<SqlAnalyzer> logger) : base(logger) { }

    public override async Task<IEnumerable<OptimizationFinding>> AnalyzeAsync(CostRecord costRecord, ResourceUsage? usage)
    {
        var findings = new List<OptimizationFinding>();
        
        try
        {
            _logger.LogInformation("Analisando SQL Database {ResourceName} com custo mensal de {Cost:C}", 
                costRecord.ResourceName, costRecord.MonthlyCost);

            if (costRecord.MonthlyCost < MINIMUM_COST_TO_ANALYZE)
            {
                return findings;
            }

            if (usage != null)
            {
                // Análise 1: Database superdimensionado (DTU baixo)
                if (usage.DtuPercentage <= LOW_DTU_THRESHOLD)
                {
                    findings.Add(CreateOversizedDatabaseFinding(costRecord, usage));
                }

                // Análise 2: Storage subutilizado
                if (usage.StoragePercentage <= LOW_STORAGE_THRESHOLD)
                {
                    findings.Add(CreateUnderutilizedStorageFinding(costRecord, usage));
                }

                // Análise 3: Candidato para Elastic Pool (se múltiplos DBs)
                if (IsElasticPoolCandidate(costRecord, usage))
                {
                    findings.Add(CreateElasticPoolOpportunityFinding(costRecord, usage));
                }

                // Análise 4: Oportunidade Reserved Capacity para DBs caros
                if (costRecord.MonthlyCost >= 500m)
                {
                    findings.Add(CreateReservedCapacityFinding(costRecord, usage));
                }

                // Análise 5: Considerar Serverless para workloads intermitentes
                if (HasIntermittentPattern(usage))
                {
                    findings.Add(CreateServerlessOpportunityFinding(costRecord, usage));
                }
            }

            return findings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao analisar SQL Database {ResourceName}", costRecord.ResourceName);
            return findings;
        }
    }

    private OptimizationFinding CreateOversizedDatabaseFinding(CostRecord costRecord, ResourceUsage usage)
    {
        var estimatedSaving = costRecord.MonthlyCost * 0.5m; // 50% economia reduzindo tier
        
        var evidence = new Dictionary<string, object>
        {
            ["avgDtuPercentage"] = usage.DtuPercentage,
            ["avgStoragePercentage"] = usage.StoragePercentage,
            ["measurementPeriod"] = "últimos 30 dias",
            ["dtuThreshold"] = LOW_DTU_THRESHOLD
        };

        return CreateFinding(
            costRecord,
            OptimizationType.SQL_OVERSIZED,
            "SQL Database superdimensionado",
            $"Database {costRecord.ResourceName} está utilizando apenas {usage.DtuPercentage:F1}% da capacidade DTU, indicando superdimensionamento.",
            "Reduzir o service tier do database (ex: de Premium P2 para P1, ou de Standard S3 para S2). Monitorar performance após a alteração.",
            estimatedSaving,
            evidence
        );
    }

    private OptimizationFinding CreateUnderutilizedStorageFinding(CostRecord costRecord, ResourceUsage usage)
    {
        var estimatedSaving = costRecord.MonthlyCost * 0.2m; // 20% economia otimizando storage
        
        var evidence = new Dictionary<string, object>
        {
            ["avgStoragePercentage"] = usage.StoragePercentage,
            ["recommendation"] = "Otimizar storage allocation ou cleanup"
        };

        return CreateFinding(
            costRecord,
            OptimizationType.SQL_OVERSIZED,
            "Storage SQL Database subutilizado",
            $"Database {costRecord.ResourceName} está usando apenas {usage.StoragePercentage:F1}% do storage provisionado.",
            "1) Reduzir o tamanho máximo do database, 2) Fazer cleanup de dados antigos, 3) Implementar archiving de dados históricos.",
            estimatedSaving,
            evidence
        );
    }

    private OptimizationFinding CreateElasticPoolOpportunityFinding(CostRecord costRecord, ResourceUsage usage)
    {
        var estimatedSaving = costRecord.MonthlyCost * 0.3m; // 30% economia com pool compartilhado
        
        var evidence = new Dictionary<string, object>
        {
            ["dtuUsage"] = usage.DtuPercentage,
            ["poolCandidateReason"] = "Baixo DTU, múltiplos DBs no mesmo server"
        };

        return CreateFinding(
            costRecord,
            OptimizationType.SQL_OVERSIZED,
            "Candidato para Elastic Pool",
            $"Database {costRecord.ResourceName} com DTU baixo pode se beneficiar de Elastic Pool se houver outros DBs similares.",
            "Avaliar consolidar múltiplos databases em um Elastic Pool para compartilhar recursos e reduzir custos.",
            estimatedSaving,
            evidence
        );
    }

    private OptimizationFinding CreateReservedCapacityFinding(CostRecord costRecord, ResourceUsage usage)
    {
        var estimatedSaving = costRecord.MonthlyCost * 0.35m; // 35% economia com reserved capacity
        
        var evidence = new Dictionary<string, object>
        {
            ["currentMonthlyCost"] = costRecord.MonthlyCost,
            ["reservedCapacitySaving"] = "35% com Reserved Capacity de 1 ano"
        };

        return CreateFinding(
            costRecord,
            OptimizationType.RESERVED_INSTANCE_OPPORTUNITY,
            "Oportunidade Reserved Capacity - SQL",
            $"SQL Database {costRecord.ResourceName} com alto custo ({costRecord.MonthlyCost:C}/mês) é candidato para Reserved Capacity.",
            "Comprar Reserved Capacity para SQL Database de 1 ou 3 anos para obter desconto significativo em workloads estáveis.",
            estimatedSaving,
            evidence
        );
    }

    private OptimizationFinding CreateServerlessOpportunityFinding(CostRecord costRecord, ResourceUsage usage)
    {
        var estimatedSaving = costRecord.MonthlyCost * 0.6m; // 60% economia com serverless
        
        var evidence = new Dictionary<string, object>
        {
            ["dtuVariation"] = "High variation indicating intermittent usage",
            ["serverlessCandidate"] = true
        };

        return CreateFinding(
            costRecord,
            OptimizationType.SQL_OVERSIZED,
            "Candidato para SQL Serverless",
            $"Database {costRecord.ResourceName} tem padrão de uso intermitente, adequado para modelo Serverless.",
            "Migrar para SQL Database Serverless para pagar apenas pelo compute utilizado. Ideal para workloads com padrões de uso variáveis.",
            estimatedSaving,
            evidence
        );
    }

    private bool IsElasticPoolCandidate(CostRecord costRecord, ResourceUsage usage)
    {
        // Lógica para determinar se é candidato para Elastic Pool
        // Por exemplo, DTU baixo + múltiplos DBs no mesmo server
        return usage.DtuPercentage <= LOW_DTU_THRESHOLD && costRecord.MonthlyCost >= 200m;
    }

    private bool HasIntermittentPattern(ResourceUsage usage)
    {
        // Simular detecção de padrão intermitente
        // Na implementação real, analisar variação de DTU ao longo do tempo
        return usage.DtuPercentage < 30.0 && usage.DtuPercentage > 0;
    }
}