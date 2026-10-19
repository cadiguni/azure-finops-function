using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class CostAllocationServiceTests
{
    private static readonly DateTime From = new(2026, 9, 1);
    private static readonly DateTime To = new(2026, 9, 3);
    private const string TagName = "CentrodeCusto";
    private const string ProductionSubscription = "00000000-0000-0000-0000-00000000a004";

    [Fact]
    public void Build_ShouldGroupCostByCostCenter()
    {
        var breakdown = Build(
            [Row("vm-a", 100m, "NAP"), Row("vm-b", 50m, "NAP"), Row("vm-c", 70m, "Digital")],
            []);

        breakdown.CostCenters.Should().HaveCount(2);
        breakdown.CostCenters[0].CostCenter.Should().Be("NAP", "a lista vem ordenada por custo");
        breakdown.CostCenters[0].TotalCost.Should().Be(150m);
        breakdown.CostCenters[0].ResourceCount.Should().Be(2);
        breakdown.TotalCost.Should().Be(220m);
    }

    [Fact]
    public void Build_ShouldSumTheSameResourceAcrossTheDaysOfThePeriod()
    {
        // Três dias do mesmo recurso viram um item de 30, não três itens de 10.
        var breakdown = Build(
            [Row("vm-a", 10m, "NAP"), Row("vm-a", 10m, "NAP"), Row("vm-a", 10m, "NAP")],
            []);

        var group = breakdown.CostCenters.Single();
        group.ResourceCount.Should().Be(1);
        group.ResourceGroups.Single().Resources.Single().TotalCost.Should().Be(30m);
    }

    [Fact]
    public void Build_ShouldKeepUntaggedCostInItsOwnBucket()
    {
        var breakdown = Build([Row("vm-a", 100m, "NAP"), Row("vm-b", 300m)], []);

        breakdown.AssignedCost.Should().Be(100m);
        breakdown.UnassignedCost.Should().Be(300m);
        breakdown.Unassigned.ResourceCount.Should().Be(1);
        breakdown.Unassigned.ResourceGroups.Single().Resources.Single().Label.Should().Be("vm-b");
        breakdown.CostCenters.Should().NotContain(group => group.CostCenter == "Não atribuído");
        breakdown.CoveragePercent.Should().Be(25m);
    }

    [Fact]
    public void Build_ShouldNotSpreadUnassignedCostOverTheCostCenters()
    {
        var breakdown = Build([Row("vm-a", 100m, "NAP"), Row("vm-b", 300m)], []);

        breakdown.CostCenters.Single().TotalCost.Should().Be(100m);
        (breakdown.AssignedCost + breakdown.UnassignedCost).Should().Be(breakdown.TotalCost);
    }

    [Fact]
    public void Build_ShouldReportHowMuchCameFromManualRules()
    {
        var rules = new[] { Rule(CostAllocationRuleTypes.ResourceGroup, "rg-erp", "ERP") };
        var breakdown = Build(
            [Row("vm-a", 100m, "NAP"), Row("vm-b", 400m, resourceGroup: "rg-erp")],
            rules);

        breakdown.ManualCost.Should().Be(400m);
        breakdown.RulesApplied.Should().Be(1);
        breakdown.CostCenters.Single(group => group.CostCenter == "ERP").ManualCost.Should().Be(400m);
    }

    [Fact]
    public void Build_ShouldFlagResourcesWhoseTagDisagreesWithTheAppliedCostCenter()
    {
        var rules = new[] { Rule(CostAllocationRuleTypes.ResourceGroup, "rg-erp", "ERP") };
        var breakdown = Build([Row("vm-a", 100m, "Antigo", resourceGroup: "rg-erp")], rules);

        var resource = breakdown.CostCenters.Single().ResourceGroups.Single().Resources.Single();
        resource.OverridesTag.Should().BeTrue();
        resource.TagValue.Should().Be("Antigo");
        resource.Source.Should().Be(CostAllocationSources.ResourceGroupRule);
    }

    [Fact]
    public void Build_ShouldNotFlagAnOverrideWhenTheRuleAgreesWithTheTag()
    {
        var rules = new[] { Rule(CostAllocationRuleTypes.ResourceGroup, "rg-erp", "ERP") };
        var breakdown = Build([Row("vm-a", 100m, "erp", resourceGroup: "rg-erp")], rules);

        breakdown.CostCenters.Single().ResourceGroups.Single().Resources.Single().OverridesTag.Should().BeFalse();
    }

    [Fact]
    public void Build_ShouldTreatCostCenterCaseInsensitively()
    {
        var breakdown = Build([Row("vm-a", 10m, "NAP"), Row("vm-b", 20m, "nap")], []);

        breakdown.CostCenters.Should().ContainSingle().Which.TotalCost.Should().Be(30m);
    }

    [Fact]
    public void Build_ShouldMergeCostCentersThatDifferOnlyByInvisibleCharacters()
    {
        // O caso real: dois grupos "TI" na tela porque uma tag tinha espaço sobrando.
        var breakdown = Build(
            [Row("vm-a", 10m, "TI"), Row("vm-b", 20m, " TI "), Row("vm-c", 30m, "TI ")],
            []);

        var group = breakdown.CostCenters.Should().ContainSingle().Subject;
        group.TotalCost.Should().Be(60m);
        group.CostCenter.Should().Be("TI");
    }

    [Fact]
    public void Build_ShouldCollapseRepeatedSpacesInsideTheCostCenterName()
    {
        var breakdown = Build(
            [Row("vm-a", 10m, "642 - GestaoAVista"), Row("vm-b", 20m, "642  -  GestaoAVista")],
            []);

        breakdown.CostCenters.Should().ContainSingle().Which.TotalCost.Should().Be(30m);
    }

    [Fact]
    public void Build_ShouldListTheSpellingsThatFedEachCostCenter()
    {
        var breakdown = Build([Row("vm-a", 10m, "TI"), Row("vm-b", 20m, "ti")], []);

        breakdown.CostCenters.Single().Variants.Should().BeEquivalentTo(["TI", "ti"]);
    }

    [Fact]
    public void Build_ShouldGroupResourcesByResourceGroupInsideTheCostCenter()
    {
        var breakdown = Build(
            [
                Row("vm-a", 10m, "TI", resourceGroup: "rg-um"),
                Row("vm-b", 20m, "TI", resourceGroup: "rg-um"),
                Row("vm-c", 70m, "TI", resourceGroup: "rg-dois")
            ],
            []);

        var groups = breakdown.CostCenters.Single().ResourceGroups;
        groups.Should().HaveCount(2);
        groups[0].ResourceGroup.Should().Be("rg-dois", "a lista vem ordenada por custo");
        groups[0].TotalCost.Should().Be(70m);
        groups[1].ResourceCount.Should().Be(2);
        groups[1].Resources.Should().HaveCount(2);
    }

    [Fact]
    public void Build_ShouldGroupUnassignedResourcesByResourceGroupToo()
    {
        var breakdown = Build(
            [Row("vm-a", 10m, resourceGroup: "rg-um"), Row("vm-b", 20m, resourceGroup: "rg-um")],
            []);

        var group = breakdown.Unassigned.ResourceGroups.Should().ContainSingle().Subject;
        group.ResourceGroup.Should().Be("rg-um");
        group.TotalCost.Should().Be(30m);
    }

    [Fact]
    public void Build_ShouldIgnoreRowsInAMinorityCurrency()
    {
        // Misturar moedas somaria unidades diferentes; o rateio fica na moeda dominante.
        var minority = Row("vm-usd", 900m, "NAP");
        minority.Currency = "USD";

        var breakdown = Build([Row("vm-a", 1000m, "NAP"), minority], []);

        breakdown.Currency.Should().Be("BRL");
        breakdown.TotalCost.Should().Be(1000m);
    }

    [Fact]
    public void Build_ShouldReturnNullCoverageWhenThereIsNoCost()
    {
        var breakdown = Build([], []);

        breakdown.TotalCost.Should().Be(0m);
        breakdown.CoveragePercent.Should().BeNull();
        breakdown.CostCenters.Should().BeEmpty();
    }

    private static CostAllocationBreakdown Build(
        IReadOnlyList<CostByResourceRow> rows,
        IReadOnlyList<CostAllocationRule> rules) =>
        CostAllocationService.Build(From, To, ProductionSubscription, rows, rules, TagName);

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
        decimal totalCost,
        string? costCenter = null,
        string resourceGroup = "rg-default") => new()
    {
        Label = label,
        ResourceId = $"/subscriptions/{ProductionSubscription}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{label}",
        ServiceName = "Virtual Machines",
        ResourceGroup = resourceGroup,
        CostCenter = costCenter ?? string.Empty,
        TotalCost = totalCost,
        Currency = "BRL",
        SubscriptionId = ProductionSubscription
    };
}
