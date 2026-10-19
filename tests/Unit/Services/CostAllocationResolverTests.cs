using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class CostAllocationResolverTests
{
    private const string TagName = "CentrodeCusto";
    private const string ProductionSubscription = "00000000-0000-0000-0000-00000000a004";

    [Fact]
    public void Resolve_ShouldUseTagValueWhenNoRuleMatches()
    {
        var assignment = CostAllocationResolver.Resolve(Row("vm-a", costCenter: "NAP"), [], TagName);

        assignment.CostCenter.Should().Be("NAP");
        assignment.Source.Should().Be(CostAllocationSources.Tag);
        assignment.RuleId.Should().BeNull();
    }

    [Fact]
    public void Resolve_ShouldReturnUnassignedWhenThereIsNoTagAndNoRule()
    {
        var assignment = CostAllocationResolver.Resolve(Row("vm-a"), [], TagName);

        assignment.IsAssigned.Should().BeFalse();
        assignment.Source.Should().Be(CostAllocationSources.Unassigned);
    }

    [Fact]
    public void Resolve_ShouldFallBackToRawTagsWhenCostCenterFieldIsEmpty()
    {
        // Snapshots importados antes do campo costCenter guardam só a tag bruta.
        var row = Row("legacy");
        row.Tags = """{"CentrodeCusto":"Centris"}""";

        var assignment = CostAllocationResolver.Resolve(row, [], TagName);

        assignment.CostCenter.Should().Be("Centris");
        assignment.Source.Should().Be(CostAllocationSources.Tag);
    }

    [Fact]
    public void Resolve_ShouldNormalizeOutdatedTagValueThroughAlias()
    {
        var rules = new[] { Rule(CostAllocationRuleTypes.TagAlias, "ti-infra", "Infraestrutura") };

        var assignment = CostAllocationResolver.Resolve(Row("vm-a", costCenter: "TI-Infra"), rules, TagName);

        assignment.CostCenter.Should().Be("Infraestrutura");
        assignment.Source.Should().Be(CostAllocationSources.TagAlias);
        assignment.RawTagValue.Should().Be("TI-Infra", "a tag original continua visível para a divergência ser auditável");
    }

    [Fact]
    public void Resolve_ShouldLetResourceGroupRuleOverrideAnOutdatedTag()
    {
        var rules = new[] { Rule(CostAllocationRuleTypes.ResourceGroup, "rg-erp-prod", "ERP") };

        var assignment = CostAllocationResolver.Resolve(
            Row("vm-a", costCenter: "Antigo", resourceGroup: "rg-erp-prod"),
            rules,
            TagName);

        assignment.CostCenter.Should().Be("ERP");
        assignment.Source.Should().Be(CostAllocationSources.ResourceGroupRule);
    }

    [Fact]
    public void Resolve_ShouldPreferResourceGroupOverNamePrefixAndService()
    {
        var rules = new[]
        {
            Rule(CostAllocationRuleTypes.Service, "Virtual Machines", "Compartilhado"),
            Rule(CostAllocationRuleTypes.NamePrefix, "vm-erp-", "Prefixo"),
            Rule(CostAllocationRuleTypes.ResourceGroup, "rg-erp-prod", "ERP")
        };

        var assignment = CostAllocationResolver.Resolve(
            Row("vm-erp-01", resourceGroup: "rg-erp-prod", serviceName: "Virtual Machines"),
            rules,
            TagName);

        assignment.CostCenter.Should().Be("ERP");
    }

    [Fact]
    public void Resolve_ShouldPreferNamePrefixOverService()
    {
        var rules = new[]
        {
            Rule(CostAllocationRuleTypes.Service, "Virtual Machines", "Compartilhado"),
            Rule(CostAllocationRuleTypes.NamePrefix, "vm-erp-", "ERP")
        };

        var assignment = CostAllocationResolver.Resolve(
            Row("vm-erp-01", serviceName: "Virtual Machines"),
            rules,
            TagName);

        assignment.CostCenter.Should().Be("ERP");
        assignment.Source.Should().Be(CostAllocationSources.NamePrefixRule);
    }

    [Fact]
    public void Resolve_ShouldPreferTheLongestPrefixAmongCompetingRules()
    {
        var rules = new[]
        {
            Rule(CostAllocationRuleTypes.NamePrefix, "sql-", "TI"),
            Rule(CostAllocationRuleTypes.NamePrefix, "sql-erp-", "ERP")
        };

        var assignment = CostAllocationResolver.Resolve(Row("sql-erp-01"), rules, TagName);

        assignment.CostCenter.Should().Be("ERP", "o prefixo mais longo é o mais específico");
    }

    [Fact]
    public void Resolve_ShouldMatchPrefixAgainstTheResourceIdWhenTheLabelIsEmpty()
    {
        var row = Row(string.Empty);
        row.ResourceId = $"/subscriptions/{ProductionSubscription}/resourceGroups/rg-a/providers/Microsoft.Sql/servers/sql-erp-01";
        var rules = new[] { Rule(CostAllocationRuleTypes.NamePrefix, "sql-erp-", "ERP") };

        CostAllocationResolver.Resolve(row, rules, TagName).CostCenter.Should().Be("ERP");
    }

    [Fact]
    public void Resolve_ShouldIgnoreDisabledRules()
    {
        var rule = Rule(CostAllocationRuleTypes.ResourceGroup, "rg-erp-prod", "ERP");
        rule.Enabled = false;

        var assignment = CostAllocationResolver.Resolve(
            Row("vm-a", costCenter: "NAP", resourceGroup: "rg-erp-prod"),
            [rule],
            TagName);

        assignment.CostCenter.Should().Be("NAP");
    }

    [Fact]
    public void Resolve_ShouldIgnoreRulesScopedToAnotherSubscription()
    {
        var rule = Rule(CostAllocationRuleTypes.ResourceGroup, "rg-erp-prod", "ERP");
        rule.SubscriptionId = "00000000-0000-0000-0000-000000000000";

        var assignment = CostAllocationResolver.Resolve(
            Row("vm-a", resourceGroup: "rg-erp-prod"),
            [rule],
            TagName);

        assignment.IsAssigned.Should().BeFalse();
    }

    [Fact]
    public void Resolve_ShouldPreferTheSubscriptionScopedRuleOverTheGlobalOne()
    {
        var global = Rule(CostAllocationRuleTypes.ResourceGroup, "rg-erp-prod", "Compartilhado");
        var scoped = Rule(CostAllocationRuleTypes.ResourceGroup, "rg-erp-prod", "ERP");
        scoped.SubscriptionId = ProductionSubscription;

        var assignment = CostAllocationResolver.Resolve(
            Row("vm-a", resourceGroup: "rg-erp-prod"),
            [global, scoped],
            TagName);

        assignment.CostCenter.Should().Be("ERP");
    }

    private static CostAllocationRule Rule(string type, string match, string costCenter) => new()
    {
        Id = $"{type}:{match}".ToLowerInvariant(),
        Type = type,
        Match = match,
        CostCenter = costCenter,
        Enabled = true
    };

    private static CostByResourceRow Row(
        string label,
        string? costCenter = null,
        string resourceGroup = "rg-default",
        string serviceName = "Virtual Machines") => new()
    {
        Label = label,
        ResourceId = $"/subscriptions/{ProductionSubscription}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{label}",
        ServiceName = serviceName,
        ResourceGroup = resourceGroup,
        CostCenter = costCenter ?? string.Empty,
        Currency = "BRL",
        SubscriptionId = ProductionSubscription
    };
}
