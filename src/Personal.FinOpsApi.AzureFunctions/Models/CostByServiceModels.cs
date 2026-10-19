using System.Text.Json.Serialization;

namespace Personal.FinOpsApi.AzureFunctions.Models;

// O serializer do worker isolated não converte PascalCase: os nomes consumidos pelo portal
// precisam ser declarados explicitamente em camelCase.

public class CostByServiceRow
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "BRL";

    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;

    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; set; }
}

public class CostByServiceTrendRow
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "BRL";
}

public class CostByServiceQueryRecord
{
    public string Label { get; set; } = string.Empty;
    public decimal TotalCost { get; set; }
    public string Currency { get; set; } = "BRL";
    public DateTime? UsageDate { get; set; }
    public int Count { get; set; } = 1;
    public string SubscriptionId { get; set; } = string.Empty;
}

public class CostByServiceQueryResponse
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string Currency { get; set; } = "BRL";
    public List<CostByServiceQueryRecord> Rows { get; set; } = new();
    public string RawJson { get; set; } = string.Empty;
}

public class CostByResourceRow
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("resourceId")]
    public string ResourceId { get; set; } = string.Empty;

    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; } = string.Empty;

    [JsonPropertyName("resourceGroup")]
    public string ResourceGroup { get; set; } = string.Empty;

    [JsonPropertyName("resourceLocation")]
    public string ResourceLocation { get; set; } = string.Empty;

    [JsonPropertyName("resourceType")]
    public string ResourceType { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public string Tags { get; set; } = string.Empty;

    /// <summary>
    /// Valor da tag de rateio resolvido na importação. Vazio quando o recurso não tem a tag —
    /// ausência de atribuição, que é diferente de custo zero.
    /// </summary>
    [JsonPropertyName("costCenter")]
    public string CostCenter { get; set; } = string.Empty;

    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "BRL";

    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;

    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; set; }

    /// <summary>Data efetiva do snapshot que forneceu este custo.</summary>
    [JsonPropertyName("dataAsOf")]
    public DateTime? DataAsOf { get; set; }

    /// <summary>Fonte financeira do snapshot que forneceu este custo.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("isStale")]
    public bool IsStale { get; set; }
}

/// <summary>
/// Custo somado por resource group. É o nível que a grade abre primeiro: milhares de recursos numa
/// página só demoram a carregar e escondem para onde o dinheiro vai.
/// </summary>
public class CostByResourceGroupRow
{
    [JsonPropertyName("resourceGroup")]
    public string ResourceGroup { get; set; } = string.Empty;

    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "BRL";

    [JsonPropertyName("resourceCount")]
    public int ResourceCount { get; set; }

    [JsonPropertyName("serviceCount")]
    public int ServiceCount { get; set; }

    /// <summary>Serviço que responde por mais custo no grupo, para a linha já dizer do que se trata.</summary>
    [JsonPropertyName("topService")]
    public string TopService { get; set; } = string.Empty;

    [JsonPropertyName("dataAsOf")]
    public DateTime? DataAsOf { get; set; }

    [JsonPropertyName("isStale")]
    public bool IsStale { get; set; }
}

public sealed class CostApiResponse<T>
{
    [JsonPropertyName("dataAsOf")]
    public DateTime? DataAsOf { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = CostDataSources.CostManagementQueryApi;

    [JsonPropertyName("items")]
    public IReadOnlyList<T> Items { get; init; } = [];

    [JsonPropertyName("allocation")]
    public IReadOnlyList<CostAllocationSummary> Allocation { get; init; } = [];

    [JsonPropertyName("sourceByDate")]
    public IReadOnlyDictionary<string, string> SourceByDate { get; init; } =
        new Dictionary<string, string>();

    [JsonPropertyName("availableDates")]
    public IReadOnlyList<string> AvailableDates { get; init; } = [];

    [JsonPropertyName("missingDates")]
    public IReadOnlyList<string> MissingDates { get; init; } = [];

    [JsonPropertyName("requestedDate")]
    public DateTime? RequestedDate { get; init; }

    [JsonPropertyName("totalCount")]
    public int? TotalCount { get; init; }

    [JsonPropertyName("page")]
    public int? Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int? PageSize { get; init; }
}

public class CostByResourceTrendRow
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "BRL";
}

public class CostByResourceQueryRecord
{
    public string Label { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public decimal TotalCost { get; set; }
    public string Currency { get; set; } = "BRL";
    public DateTime? UsageDate { get; set; }
    public int Count { get; set; } = 1;
    public string SubscriptionId { get; set; } = string.Empty;
}

public class CostByResourceQueryResponse
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string Currency { get; set; } = "BRL";
    public List<CostByResourceQueryRecord> Rows { get; set; } = new();
    public string RawJson { get; set; } = string.Empty;
}
