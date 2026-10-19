using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class CostAllocationCoverageServiceTests
{
    private static readonly DateTime Date = new(2026, 9, 8);
    private const string TagName = "CentrodeCusto";

    [Fact]
    public void BuildCoverage_ShouldSeparateTaggedFromUntaggedCost()
    {
        var coverage = Build(
            [Row("db-a", 100m, "NAP"), Row("vm-b", 300m, costCenter: null)],
            []);

        var subscription = coverage.Subscriptions.Single();
        subscription.TaggedCost.Should().Be(100m);
        subscription.UntaggedCost.Should().Be(300m);
        subscription.TaggedResourceCount.Should().Be(1);
        subscription.UntaggedResourceCount.Should().Be(1);
    }

    [Fact]
    public void BuildCoverage_ShouldGroupCostByCostCenterValue()
    {
        var coverage = Build(
            [Row("a", 100m, "NAP"), Row("b", 50m, "NAP"), Row("c", 70m, "Digital")],
            []);

        var centers = coverage.Subscriptions.Single().CostCenters;
        centers.Single(item => item.Value == "NAP").TotalCost.Should().Be(150m);
        centers.Single(item => item.Value == "NAP").ResourceCount.Should().Be(2);
        centers.Single(item => item.Value == "Digital").TotalCost.Should().Be(70m);
        centers.First().Value.Should().Be("NAP", "a lista vem ordenada por custo");
    }

    [Fact]
    public void BuildCoverage_ShouldTreatCostCenterCaseInsensitively()
    {
        var coverage = Build([Row("a", 10m, "NAP"), Row("b", 20m, "nap")], []);

        coverage.Subscriptions.Single().CostCenters.Should().ContainSingle()
            .Which.TotalCost.Should().Be(30m);
    }

    [Fact]
    public void BuildCoverage_ShouldFallBackToRawTagsOnRowsImportedBeforeTheCostCenterField()
    {
        // Blobs históricos não têm costCenter, mas guardam a tag bruta: o dado não se perde.
        var legacy = Row("legacy", 80m, costCenter: null);
        legacy.Tags = """{"CentrodeCusto":"Centris"}""";

        var coverage = Build([legacy], []);

        coverage.Subscriptions.Single().TaggedCost.Should().Be(80m);
        coverage.Subscriptions.Single().CostCenters.Single().Value.Should().Be("Centris");
    }

    [Fact]
    public void BuildCoverage_ShouldKeepUnallocatedCostSeparateFromUntaggedCost()
    {
        // 400 de custo em recursos (dos quais 300 sem tag) e 500 no total da assinatura:
        // os 100 restantes não têm recurso para taggear, o que é um problema diferente.
        var coverage = Build(
            [Row("db-a", 100m, "NAP"), Row("vm-b", 300m, costCenter: null)],
            [Allocation(total: 500m, unallocated: 100m)]);

        var subscription = coverage.Subscriptions.Single();
        subscription.ResourceCost.Should().Be(400m);
        subscription.UntaggedCost.Should().Be(300m);
        subscription.UnallocatedCost.Should().Be(100m);
        subscription.TotalCost.Should().Be(500m);
    }

    [Fact]
    public void BuildCoverage_ShouldReportUnknownTotalsWhenAllocationIsMissing()
    {
        var coverage = Build([Row("db-a", 100m, "NAP")], []);

        coverage.Subscriptions.Single().TotalCost.Should().BeNull();
        coverage.Subscriptions.Single().UnallocatedCost.Should().BeNull();
    }

    [Fact]
    public void BuildCoverage_ShouldTreatBlankTagAsUntagged()
    {
        var coverage = Build([Row("a", 10m, "   ")], []);

        coverage.Subscriptions.Single().TaggedCost.Should().Be(0m);
        coverage.Subscriptions.Single().UntaggedCost.Should().Be(10m);
    }

    [Fact]
    public void BuildCoverage_ShouldFlagDayWithoutCostData()
    {
        var coverage = CostAllocationCoverageService.BuildCoverage(Date, [], [], TagName);

        coverage.HasCostData.Should().BeFalse();
        coverage.Subscriptions.Should().BeEmpty();
        coverage.TagName.Should().Be(TagName);
    }

    [Fact]
    public void BuildCoverage_ShouldKeepOnlyTheDominantCurrency()
    {
        var usd = Row("usd", 900m, "NAP");
        usd.Currency = "USD";

        var coverage = Build([Row("brl", 100m, "NAP"), usd], []);

        coverage.Currency.Should().Be("USD");
        coverage.Subscriptions.Single().ResourceCost.Should().Be(900m);
    }

    private static CostAllocationCoverageDaily Build(
        IReadOnlyList<CostByResourceRow> rows,
        IReadOnlyList<CostAllocationSummary> allocations)
        => CostAllocationCoverageService.BuildCoverage(Date, rows, allocations, TagName);

    private static CostByResourceRow Row(string name, decimal cost, string? costCenter) => new()
    {
        Label = name,
        ResourceId = $"/subscriptions/sub-001/resourceGroups/rg/providers/x/{name}",
        ResourceGroup = "rg",
        SubscriptionId = "sub-001",
        Currency = "BRL",
        TotalCost = cost,
        CostCenter = costCenter ?? string.Empty
    };

    private static CostAllocationSummary Allocation(decimal total, decimal unallocated) => new()
    {
        SubscriptionId = "sub-001",
        UsageDate = Date,
        Currency = "BRL",
        TotalCost = total,
        AllocatedResourceCost = total - unallocated,
        UnallocatedCost = unallocated
    };
}
