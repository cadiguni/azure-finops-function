using System.Text.Json.Serialization;

namespace Personal.FinOpsApi.AzureFunctions.Models;

public static class CostAllocationCoverageConstants
{
    public const string ScopeKey = "costAllocationCoverage";
    public const string DocumentType = "costAllocationCoverageDaily";
}

/// <summary>
/// Quanto do custo de um dia é atribuível a um centro de custo. Consolidado por timer, junto do
/// rollup de bancos, para a API não varrer os snapshots por recurso a cada requisição.
/// </summary>
public sealed class CostAllocationCoverageDaily
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("scopeKey")]
    public string ScopeKey { get; set; } = CostAllocationCoverageConstants.ScopeKey;

    [JsonPropertyName("type")]
    public string Type { get; set; } = CostAllocationCoverageConstants.DocumentType;

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "BRL";

    /// <summary>Nome da tag usada na apuração, para o número não ser reinterpretado fora de contexto.</summary>
    [JsonPropertyName("tagName")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("hasCostData")]
    public bool HasCostData { get; set; }

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("subscriptions")]
    public List<CostAllocationCoverageSubscription> Subscriptions { get; set; } = [];

    public static string BuildId(DateTime date) =>
        $"{CostAllocationCoverageConstants.ScopeKey}-{date:yyyy-MM-dd}";
}

public sealed class CostAllocationCoverageSubscription
{
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>Total da assinatura no dia, vindo da alocação. Null quando não publicada.</summary>
    [JsonPropertyName("totalCost")]
    public decimal? TotalCost { get; set; }

    /// <summary>Custo que o export conseguiu associar a algum recurso.</summary>
    [JsonPropertyName("resourceCost")]
    public decimal ResourceCost { get; set; }

    /// <summary>
    /// Custo sem recurso identificado. Diferente de custo sem tag: aqui nem recurso existe para taggear.
    /// </summary>
    [JsonPropertyName("unallocatedCost")]
    public decimal? UnallocatedCost { get; set; }

    [JsonPropertyName("taggedCost")]
    public decimal TaggedCost { get; set; }

    [JsonPropertyName("untaggedCost")]
    public decimal UntaggedCost { get; set; }

    [JsonPropertyName("taggedResourceCount")]
    public int TaggedResourceCount { get; set; }

    [JsonPropertyName("untaggedResourceCount")]
    public int UntaggedResourceCount { get; set; }

    [JsonPropertyName("costCenters")]
    public List<CostAllocationCoverageCostCenter> CostCenters { get; set; } = [];
}

/// <summary>Resposta agregada do período, consumida pelo portal.</summary>
public sealed class CostAllocationCoverageSummary
{
    [JsonPropertyName("from")]
    public string From { get; init; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; init; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "BRL";

    [JsonPropertyName("tagName")]
    public string TagName { get; init; } = string.Empty;

    [JsonPropertyName("daysWithoutData")]
    public int DaysWithoutData { get; init; }

    [JsonPropertyName("taggedCost")]
    public decimal TaggedCost { get; init; }

    [JsonPropertyName("untaggedCost")]
    public decimal UntaggedCost { get; init; }

    /// <summary>Cobertura por custo, não por contagem. Null quando não há custo por recurso no período.</summary>
    [JsonPropertyName("coveragePercent")]
    public decimal? CoveragePercent { get; init; }

    [JsonPropertyName("subscriptions")]
    public IReadOnlyList<CostAllocationCoverageSubscriptionTotal> Subscriptions { get; init; } = [];
}

public sealed class CostAllocationCoverageSubscriptionTotal
{
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; init; } = string.Empty;

    [JsonPropertyName("totalCost")]
    public decimal? TotalCost { get; init; }

    [JsonPropertyName("resourceCost")]
    public decimal ResourceCost { get; init; }

    [JsonPropertyName("unallocatedCost")]
    public decimal? UnallocatedCost { get; init; }

    [JsonPropertyName("taggedCost")]
    public decimal TaggedCost { get; init; }

    [JsonPropertyName("untaggedCost")]
    public decimal UntaggedCost { get; init; }

    [JsonPropertyName("costCenters")]
    public IReadOnlyList<CostAllocationCoverageCostCenter> CostCenters { get; init; } = [];
}

public sealed class CostAllocationCoverageCostCenter
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }

    [JsonPropertyName("resourceCount")]
    public int ResourceCount { get; set; }
}
