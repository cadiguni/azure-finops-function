using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class RecommendationCostAccountingTests
{
    [Fact]
    public void ObservedTotal_ShouldCountSharedCostOnce()
    {
        var recommendations = new[]
        {
            Recommendation("app-a", CostMatchSources.ParentResource, 120m, 30m),
            Recommendation("app-b", CostMatchSources.ParentResource, 120m, 40m)
        };

        RecommendationCostAccounting.ObservedTotal(recommendations).Should().Be(120m);
    }

    [Fact]
    public void Savings_ShouldNotAttributeSharedCostToChildren()
    {
        var recommendations = new[]
        {
            Recommendation("app-a", CostMatchSources.ParentResource, 120m, 30m),
            Recommendation("app-b", CostMatchSources.ParentResource, 120m, 40m)
        };

        RecommendationCostAccounting.Savings(recommendations).Should().Be(0m);
    }

    [Fact]
    public void Savings_ShouldUseHighestAlternativeForSameDirectResource()
    {
        var recommendations = new[]
        {
            Recommendation("plan-a", CostMatchSources.ResourceId, 120m, 30m),
            Recommendation("plan-a", CostMatchSources.ResourceId, 120m, 40m)
        };

        RecommendationCostAccounting.Savings(recommendations).Should().Be(40m);
    }

    [Fact]
    public void ObservedTotal_ShouldSumAcrossDatasetRevisionsOfQueuedAnalysisSteps()
    {
        var first = Recommendation("plan-a", CostMatchSources.ResourceId, 120m, 30m);
        first.CostMatchedResourceId = first.ResourceId;
        var second = Recommendation("plan-b", CostMatchSources.ResourceId, 80m, 20m, "revision-2");
        second.CostMatchedResourceId = second.ResourceId;

        RecommendationCostAccounting.ObservedTotal(new[] { first, second }).Should().Be(200m);
    }

    [Fact]
    public void Savings_ShouldTreatNameAndGroupMatchAsDirectCost()
    {
        var recommendation = Recommendation("plan-a", CostMatchSources.ResourceNameAndGroup, 120m, 30m);

        RecommendationCostAccounting.Scope(recommendation).Should().Be("Direct");
        RecommendationCostAccounting.Savings([recommendation]).Should().Be(30m);
    }

    [Fact]
    public void SavingsCurrency_ShouldFallBackToRecommendationCurrencyWhenPreferredHasNoSavings()
    {
        var brl = Recommendation("plan-brl", CostMatchSources.ResourceId, 120m, 30m);

        RecommendationCostAccounting.SavingsCurrency([brl], "USD").Should().Be("BRL");
        RecommendationCostAccounting.SavingsCurrency([brl], null).Should().Be("BRL");
    }

    [Fact]
    public void SavingsCurrency_ShouldPreferFinancialCurrencyWhenQuantified()
    {
        var brl = Recommendation("plan-brl", CostMatchSources.ResourceId, 120m, 30m);
        var usdA = Recommendation("plan-usd-a", CostMatchSources.ResourceId, 80m, 20m);
        usdA.CostCurrency = "USD";
        var usdB = Recommendation("plan-usd-b", CostMatchSources.ResourceId, 80m, 20m);
        usdB.CostCurrency = "USD";

        RecommendationCostAccounting.SavingsCurrency([brl, usdA, usdB], "BRL").Should().Be("BRL");
        RecommendationCostAccounting.SavingsCurrency([brl, usdA, usdB]).Should().Be("USD");
    }

    [Fact]
    public void SavingsCurrency_WithoutQuantifiedRecommendations_ShouldBeNull()
    {
        var shared = Recommendation("app-a", CostMatchSources.ParentResource, 120m, 30m);

        RecommendationCostAccounting.SavingsCurrency([shared], "BRL").Should().BeNull();
    }

    [Fact]
    public void ObservedTotal_ShouldKeepMatchedRowsWhenAnotherRecommendationHasNoAttributableCost()
    {
        var matched = Recommendation("plan-a", CostMatchSources.ResourceId, 120m, 30m);
        var missing = Recommendation("plan-missing", CostMatchSources.NotMatched, 80m, 20m);

        RecommendationCostAccounting.ObservedTotal([matched, missing]).Should().Be(120m);
    }

    [Fact]
    public void ObservedTotal_ShouldSumSubscriptionsWithDifferentBillingCycles()
    {
        var first = Recommendation("plan-a", CostMatchSources.ResourceId, 120m, 30m);
        first.SubscriptionId = "sub-a";
        first.CostMatchedResourceId = "/subscriptions/sub-a/resourceGroups/rg/providers/Microsoft.Web/serverfarms/plan-a";
        var second = Recommendation("plan-b", CostMatchSources.ResourceId, 80m, 20m);
        second.SubscriptionId = "sub-b";
        second.CostMatchedResourceId = "/subscriptions/sub-b/resourceGroups/rg/providers/Microsoft.Web/serverfarms/plan-b";
        second.CostPeriodFrom = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        RecommendationCostAccounting.ObservedTotal([first, second], "BRL").Should().Be(200m);
    }

    [Fact]
    public void Savings_WithCurrency_ShouldKeepQuantifiedCurrencyWithoutAddingAnotherCurrency()
    {
        var brl = Recommendation("plan-brl", CostMatchSources.ResourceId, 120m, 30m);
        var usd = Recommendation("plan-usd", CostMatchSources.ResourceId, 80m, 20m);
        usd.CostCurrency = "USD";

        RecommendationCostAccounting.Savings([brl, usd], "BRL").Should().Be(30m);
        RecommendationCostAccounting.Savings([brl, usd]).Should().Be(0m);
    }

    private static CostRecommendation Recommendation(
        string resourceName,
        string costSource,
        decimal observedCost,
        decimal savings,
        string datasetRevision = "revision-1") => new()
    {
        ResourceId = $"/subscriptions/sub-001/resourceGroups/rg/providers/Microsoft.Web/sites/{resourceName}",
        ResourceName = resourceName,
        SubscriptionId = "sub-001",
        CostMatchedResourceId = "/subscriptions/sub-001/resourceGroups/rg/providers/Microsoft.Web/serverfarms/shared-plan",
        CostSource = costSource,
        ObservedCost = observedCost,
        EstimatedMonthlyCost = observedCost,
        EstimatedMonthlySavings = savings,
        CostCurrency = "BRL",
        CostPeriodFrom = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
        CostPeriodTo = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc),
        CostProvenance = new CostReadProvenance { DatasetRevision = datasetRevision }
    };
}
