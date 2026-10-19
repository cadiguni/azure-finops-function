using System.Text.Json.Serialization;

namespace Personal.FinOpsApi.AzureFunctions.Models;

/// <summary>
/// Como o centro de custo de um recurso foi decidido. Fica junto do valor porque rateio sem
/// procedência não se audita: quem revisa precisa saber se veio da tag ou de um ajuste manual.
/// </summary>
public static class CostAllocationSources
{
    public const string ResourceGroupRule = "ResourceGroupRule";
    public const string NamePrefixRule = "NamePrefixRule";
    public const string ServiceRule = "ServiceRule";
    public const string TagAlias = "TagAlias";
    public const string Tag = "Tag";
    public const string Unassigned = "Unassigned";
}

/// <summary>
/// Tipos de ajuste manual. A ordem de precedência entre eles está em
/// <see cref="Services.CostAllocationResolver"/> — do mais específico para o mais genérico.
/// </summary>
public static class CostAllocationRuleTypes
{
    /// <summary>Nome exato do resource group.</summary>
    public const string ResourceGroup = "ResourceGroup";

    /// <summary>Prefixo do nome do recurso (ex.: "sql-erp-").</summary>
    public const string NamePrefix = "NamePrefix";

    /// <summary>Nome exato do serviço do Cost Management (ex.: "Azure Database for PostgreSQL").</summary>
    public const string Service = "Service";

    /// <summary>Valor bruto da tag que deve ser lido como outro centro de custo.</summary>
    public const string TagAlias = "TagAlias";

    public static readonly IReadOnlyList<string> All =
        [ResourceGroup, NamePrefix, Service, TagAlias];

    public static bool IsKnown(string? value) =>
        All.Any(item => item.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string? Normalize(string? value) =>
        All.FirstOrDefault(item => item.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed class CostAllocationRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = CostAllocationRuleTypes.ResourceGroup;

    /// <summary>Padrão comparado conforme o tipo: nome do RG, prefixo, serviço ou valor de tag.</summary>
    [JsonPropertyName("match")]
    public string Match { get; set; } = string.Empty;

    /// <summary>Centro de custo canônico que a regra atribui.</summary>
    [JsonPropertyName("costCenter")]
    public string CostCenter { get; set; } = string.Empty;

    /// <summary>Restringe a regra a uma assinatura. Vazio aplica a todas.</summary>
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Justificativa do ajuste, para a revisão futura não depender de memória.</summary>
    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = "system";

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedBy")]
    public string UpdatedBy { get; set; } = "system";
}

public sealed class CostAllocationRulesConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedBy")]
    public string UpdatedBy { get; set; } = "system";

    [JsonPropertyName("rules")]
    public List<CostAllocationRule> Rules { get; set; } = [];
}

/// <summary>Centro de custo resolvido para uma linha de custo, com a origem da decisão.</summary>
public readonly record struct CostAllocationAssignment(
    string? CostCenter,
    string Source,
    string? RuleId,
    string? RawTagValue)
{
    public bool IsAssigned => !string.IsNullOrWhiteSpace(CostCenter);
}

public sealed class CostAllocationResourceItem
{
    [JsonPropertyName("resourceId")]
    public string ResourceId { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; } = string.Empty;

    [JsonPropertyName("resourceGroup")]
    public string ResourceGroup { get; set; } = string.Empty;

    [JsonPropertyName("resourceType")]
    public string ResourceType { get; set; } = string.Empty;

    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "BRL";

    /// <summary>Como o centro de custo foi decidido — ver <see cref="CostAllocationSources"/>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = CostAllocationSources.Unassigned;

    [JsonPropertyName("ruleId")]
    public string? RuleId { get; set; }

    /// <summary>Valor bruto da tag, mesmo quando um ajuste manual prevaleceu sobre ele.</summary>
    [JsonPropertyName("tagValue")]
    public string? TagValue { get; set; }

    /// <summary>Verdadeiro quando a tag existe e aponta para outro centro de custo que o aplicado.</summary>
    [JsonPropertyName("overridesTag")]
    public bool OverridesTag { get; set; }
}

/// <summary>
/// Resource group dentro de um centro de custo. É o nível que a tela abre primeiro: um centro de
/// custo com duzentos recursos é ilegível, e o RG é a unidade com que a infraestrutura já pensa.
/// </summary>
public sealed class CostAllocationResourceGroup
{
    [JsonPropertyName("resourceGroup")]
    public string ResourceGroup { get; set; } = string.Empty;

    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }

    [JsonPropertyName("resourceCount")]
    public int ResourceCount { get; set; }

    [JsonPropertyName("resources")]
    public List<CostAllocationResourceItem> Resources { get; set; } = [];
}

public sealed class CostAllocationGroup
{
    [JsonPropertyName("costCenter")]
    public string CostCenter { get; set; } = string.Empty;

    /// <summary>
    /// Grafias distintas da tag que caíram neste grupo. Mais de uma quer dizer que o ambiente
    /// escreve o mesmo centro de custo de jeitos diferentes — dado de governança, não enfeite.
    /// </summary>
    [JsonPropertyName("variants")]
    public List<string> Variants { get; set; } = [];

    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }

    [JsonPropertyName("percent")]
    public decimal Percent { get; set; }

    [JsonPropertyName("resourceCount")]
    public int ResourceCount { get; set; }

    /// <summary>Quanto do valor do grupo veio de ajuste manual, em vez da tag.</summary>
    [JsonPropertyName("manualCost")]
    public decimal ManualCost { get; set; }

    [JsonPropertyName("resourceGroups")]
    public List<CostAllocationResourceGroup> ResourceGroups { get; set; } = [];
}

public sealed class CostAllocationBreakdown
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "BRL";

    [JsonPropertyName("tagName")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; set; }

    [JsonPropertyName("subscriptionIds")]
    public List<string> SubscriptionIds { get; set; } = [];

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("daysWithoutData")]
    public int DaysWithoutData { get; set; }

    /// <summary>Custo por recurso somado no período. Não é o total faturado da assinatura.</summary>
    [JsonPropertyName("totalCost")]
    public decimal TotalCost { get; set; }

    [JsonPropertyName("assignedCost")]
    public decimal AssignedCost { get; set; }

    [JsonPropertyName("unassignedCost")]
    public decimal UnassignedCost { get; set; }

    [JsonPropertyName("coveragePercent")]
    public decimal? CoveragePercent { get; set; }

    /// <summary>Parte do custo atribuída por ajuste manual, e não pela tag.</summary>
    [JsonPropertyName("manualCost")]
    public decimal ManualCost { get; set; }

    [JsonPropertyName("rulesApplied")]
    public int RulesApplied { get; set; }

    [JsonPropertyName("costCenters")]
    public List<CostAllocationGroup> CostCenters { get; set; } = [];

    /// <summary>
    /// Recursos sem tag e sem ajuste manual. Ficam num balde próprio em vez de rateados:
    /// distribuir o que não se sabe de quem é transformaria lacuna de governança em número.
    /// </summary>
    [JsonPropertyName("unassigned")]
    public CostAllocationGroup Unassigned { get; set; } = new() { CostCenter = "Não atribuído" };
}
