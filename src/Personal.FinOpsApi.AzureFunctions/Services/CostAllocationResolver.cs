using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Decide o centro de custo de cada linha de custo. Parte pura: recebe as regras e a linha, não
/// toca em storage.
///
/// Precedência, do mais específico para o mais genérico:
/// 1. Regra de resource group (nome exato);
/// 2. Regra de prefixo de nome (o prefixo mais longo vence);
/// 3. Regra de serviço (nome exato do serviço);
/// 4. Valor da tag, normalizado por um alias quando houver;
/// 5. Sem atribuição.
///
/// Ajuste manual vence a tag de propósito: as tags do ambiente estão defasadas, e corrigir a tag
/// no Azure é um processo mais lento que o fechamento do rateio. A tag bruta continua no
/// resultado (<see cref="CostAllocationAssignment.RawTagValue"/>) para a divergência ficar visível.
/// </summary>
public static class CostAllocationResolver
{
    public static CostAllocationAssignment Resolve(
        CostByResourceRow row,
        IReadOnlyList<CostAllocationRule> rules,
        string tagName)
    {
        var rawTagValue = ResolveRawTagValue(row, tagName);
        var applicable = rules
            .Where(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.CostCenter))
            .Where(rule => AppliesToSubscription(rule, row.SubscriptionId))
            .ToList();

        var resourceGroupRule = BestMatch(
            applicable,
            CostAllocationRuleTypes.ResourceGroup,
            rule => EqualsTrimmed(row.ResourceGroup, rule.Match));
        if (resourceGroupRule is not null)
        {
            return new CostAllocationAssignment(
                resourceGroupRule.CostCenter.Trim(),
                CostAllocationSources.ResourceGroupRule,
                resourceGroupRule.Id,
                rawTagValue);
        }

        var namePrefixRule = BestMatch(
            applicable,
            CostAllocationRuleTypes.NamePrefix,
            rule => StartsWithTrimmed(row.Label, rule.Match) || StartsWithTrimmed(ResourceName(row), rule.Match));
        if (namePrefixRule is not null)
        {
            return new CostAllocationAssignment(
                namePrefixRule.CostCenter.Trim(),
                CostAllocationSources.NamePrefixRule,
                namePrefixRule.Id,
                rawTagValue);
        }

        var serviceRule = BestMatch(
            applicable,
            CostAllocationRuleTypes.Service,
            rule => EqualsTrimmed(row.ServiceName, rule.Match));
        if (serviceRule is not null)
        {
            return new CostAllocationAssignment(
                serviceRule.CostCenter.Trim(),
                CostAllocationSources.ServiceRule,
                serviceRule.Id,
                rawTagValue);
        }

        if (string.IsNullOrWhiteSpace(rawTagValue))
        {
            return new CostAllocationAssignment(null, CostAllocationSources.Unassigned, null, rawTagValue);
        }

        var aliasRule = BestMatch(
            applicable,
            CostAllocationRuleTypes.TagAlias,
            rule => EqualsTrimmed(rawTagValue, rule.Match));
        return aliasRule is not null
            ? new CostAllocationAssignment(
                aliasRule.CostCenter.Trim(),
                CostAllocationSources.TagAlias,
                aliasRule.Id,
                rawTagValue)
            : new CostAllocationAssignment(
                rawTagValue.Trim(),
                CostAllocationSources.Tag,
                null,
                rawTagValue);
    }

    /// <summary>
    /// O centro de custo já vem resolvido da importação; linhas antigas caem no texto bruto da tag.
    /// </summary>
    public static string? ResolveRawTagValue(CostByResourceRow row, string tagName)
    {
        var value = string.IsNullOrWhiteSpace(row.CostCenter)
            ? CostTagParser.TryGetTagValue(row.Tags, tagName)
            : row.CostCenter;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Entre regras do mesmo tipo, o padrão mais longo vence: é o mais específico.</summary>
    private static CostAllocationRule? BestMatch(
        IReadOnlyList<CostAllocationRule> rules,
        string type,
        Func<CostAllocationRule, bool> predicate)
    {
        return rules
            .Where(rule => type.Equals(rule.Type?.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Match))
            .Where(predicate)
            // Uma regra restrita a uma assinatura é mais específica que a mesma regra global.
            .OrderByDescending(rule => string.IsNullOrWhiteSpace(rule.SubscriptionId) ? 0 : 1)
            .ThenByDescending(rule => rule.Match.Trim().Length)
            .FirstOrDefault();
    }

    private static bool AppliesToSubscription(CostAllocationRule rule, string? subscriptionId) =>
        string.IsNullOrWhiteSpace(rule.SubscriptionId)
        || EqualsTrimmed(subscriptionId, rule.SubscriptionId);

    private static bool EqualsTrimmed(string? value, string? other) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.IsNullOrWhiteSpace(other)
        && value.Trim().Equals(other.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithTrimmed(string? value, string? prefix) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.IsNullOrWhiteSpace(prefix)
        && value.Trim().StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Último segmento do resourceId: o nome do recurso como aparece no portal do Azure.</summary>
    private static string ResourceName(CostByResourceRow row)
    {
        var resourceId = row.ResourceId?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return string.Empty;
        }

        var separator = resourceId.LastIndexOf('/');
        return separator >= 0 && separator < resourceId.Length - 1
            ? resourceId[(separator + 1)..]
            : resourceId;
    }
}
