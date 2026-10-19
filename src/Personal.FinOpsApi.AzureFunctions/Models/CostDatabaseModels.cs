using System.Text.Json.Serialization;

namespace Personal.FinOpsApi.AzureFunctions.Models;

public static class CostDatabaseRollupConstants
{
    /// <summary>Partição própria dentro do container de summaries do portal.</summary>
    public const string ScopeKey = "databaseCostRollup";
    public const string DocumentType = "databaseCostDailyRollup";
}

/// <summary>
/// Consolidado diário de custo de bancos, gravado por timer. A API lê estes documentos em vez de
/// varrer os snapshots por recurso a cada requisição.
/// </summary>
public sealed class CostDatabaseDailyRollup
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("scopeKey")]
    public string ScopeKey { get; set; } = CostDatabaseRollupConstants.ScopeKey;

    [JsonPropertyName("type")]
    public string Type { get; set; } = CostDatabaseRollupConstants.DocumentType;

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "BRL";

    [JsonPropertyName("source")]
    public string Source { get; set; } = CostDataSources.CostManagementQueryApi;

    [JsonPropertyName("dataAsOf")]
    public DateTime? DataAsOf { get; set; }

    /// <summary>Falso quando o dia não tinha snapshot por recurso: ausência de dado, não custo zero.</summary>
    [JsonPropertyName("hasCostData")]
    public bool HasCostData { get; set; }

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("resources")]
    public List<CostDatabaseRollupResource> Resources { get; set; } = [];

    [JsonPropertyName("subscriptionTotals")]
    public List<CostDatabaseRollupSubscription> SubscriptionTotals { get; set; } = [];

    public static string BuildId(DateTime date) =>
        $"{CostDatabaseRollupConstants.ScopeKey}-{date:yyyy-MM-dd}";
}

public sealed class CostDatabaseRollupResource
{
    [JsonPropertyName("resourceId")]
    public string ResourceId { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("engine")]
    public string Engine { get; set; } = string.Empty;

    [JsonPropertyName("resourceGroup")]
    public string ResourceGroup { get; set; } = string.Empty;

    [JsonPropertyName("resourceType")]
    public string ResourceType { get; set; } = string.Empty;

    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }
}

public sealed class CostDatabaseRollupSubscription
{
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>Custo total da assinatura no dia. Null quando a alocação não foi publicada.</summary>
    [JsonPropertyName("subscriptionTotalCost")]
    public decimal? SubscriptionTotalCost { get; set; }
}

/// <summary>Janela consultada e a janela imediatamente anterior de mesmo tamanho.</summary>
public sealed class CostDatabaseRange
{
    [JsonPropertyName("from")]
    public string From { get; init; } = string.Empty;
    [JsonPropertyName("to")]
    public string To { get; init; } = string.Empty;
    [JsonPropertyName("previousFrom")]
    public string PreviousFrom { get; init; } = string.Empty;
    [JsonPropertyName("previousTo")]
    public string PreviousTo { get; init; } = string.Empty;
}

public sealed class CostDatabaseEngineTotal
{
    [JsonPropertyName("engine")]
    public string Engine { get; init; } = string.Empty;
    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; init; }
    [JsonPropertyName("previousTotalCost")]
    public decimal? PreviousTotalCost { get; init; }
    [JsonPropertyName("resourceCount")]
    public int ResourceCount { get; init; }
}

public sealed class CostDatabaseSubscriptionTotal
{
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; init; } = string.Empty;
    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; init; }
    [JsonPropertyName("sqlCost")]
    public decimal SqlCost { get; init; }
    [JsonPropertyName("cosmosCost")]
    public decimal CosmosCost { get; init; }

    /// <summary>Custo total da assinatura no período. Null quando a alocação não cobre o período.</summary>
    [JsonPropertyName("subscriptionTotalCost")]
    public decimal? SubscriptionTotalCost { get; init; }

    /// <summary>Participação dos bancos no custo da assinatura. Null quando o total é desconhecido.</summary>
    [JsonPropertyName("shareOfSubscriptionPercent")]
    public decimal? ShareOfSubscriptionPercent { get; init; }

    [JsonPropertyName("resourceCount")]
    public int ResourceCount { get; init; }
}

public sealed class CostDatabaseResourceTotal
{
    [JsonPropertyName("resourceId")]
    public string ResourceId { get; init; } = string.Empty;
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;
    [JsonPropertyName("engine")]
    public string Engine { get; init; } = string.Empty;
    [JsonPropertyName("resourceGroup")]
    public string ResourceGroup { get; init; } = string.Empty;
    [JsonPropertyName("resourceType")]
    public string ResourceType { get; init; } = string.Empty;
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; init; } = string.Empty;
    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; init; }
    [JsonPropertyName("previousTotalCost")]
    public decimal? PreviousTotalCost { get; init; }
    [JsonPropertyName("daysWithCost")]
    public int DaysWithCost { get; init; }
}

public sealed class CostDatabaseDailyPoint
{
    [JsonPropertyName("date")]
    public string Date { get; init; } = string.Empty;
    [JsonPropertyName("sqlCost")]
    public decimal SqlCost { get; init; }
    [JsonPropertyName("cosmosCost")]
    public decimal CosmosCost { get; init; }
}

/// <summary>
/// Retrato exclusivamente financeiro de SQL e Cosmos DB: custo observado, sem métricas de uso
/// e sem recomendação de economia.
/// </summary>
public sealed class CostDatabaseOverview
{
    [JsonPropertyName("range")]
    public CostDatabaseRange Range { get; init; } = new();
    [JsonPropertyName("dataAsOf")]
    public DateTime? DataAsOf { get; init; }
    [JsonPropertyName("source")]
    public string Source { get; init; } = CostDataSources.CostManagementQueryApi;
    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "BRL";
    [JsonPropertyName("daysWithoutData")]
    public int DaysWithoutData { get; init; }

    /// <summary>
    /// Dias do período que entraram na soma. Um período com buracos soma menos que o mesmo período
    /// no portal do Azure, e o número sozinho não denuncia isso.
    /// </summary>
    [JsonPropertyName("daysWithData")]
    public int DaysWithData { get; init; }

    [JsonPropertyName("expectedDays")]
    public int ExpectedDays { get; init; }

    /// <summary>Buracos na janela anterior, que é a base da variação exibida.</summary>
    [JsonPropertyName("previousDaysWithoutData")]
    public int PreviousDaysWithoutData { get; init; }

    [JsonPropertyName("isLatestSnapshotFallback")]
    public bool IsLatestSnapshotFallback { get; init; }
    [JsonPropertyName("engines")]
    public IReadOnlyList<CostDatabaseEngineTotal> Engines { get; init; } = [];
    [JsonPropertyName("subscriptions")]
    public IReadOnlyList<CostDatabaseSubscriptionTotal> Subscriptions { get; init; } = [];
    [JsonPropertyName("resources")]
    public IReadOnlyList<CostDatabaseResourceTotal> Resources { get; init; } = [];
    [JsonPropertyName("daily")]
    public IReadOnlyList<CostDatabaseDailyPoint> Daily { get; init; } = [];
}
