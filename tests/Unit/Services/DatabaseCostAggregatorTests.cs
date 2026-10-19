using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class DatabaseCostAggregatorTests
{
    [Fact]
    public void Build_ShouldSplitSqlAndCosmos()
    {
        var overview = Build(
            [
                Rollup("2026-09-01", Sql("db-a", 100m), Cosmos("account-a", 40m)),
                Rollup("2026-09-02", Sql("db-a", 110m), Cosmos("account-a", 30m))
            ],
            previous: []);

        Engine(overview, DatabaseResourceClassifier.SqlEngine).TotalCost.Should().Be(210m);
        Engine(overview, DatabaseResourceClassifier.CosmosEngine).TotalCost.Should().Be(70m);
        overview.Resources.Should().HaveCount(2);
    }

    [Fact]
    public void Build_ShouldCompareWithPreviousWindowPerResource()
    {
        var overview = Build(
            [Rollup("2026-09-01", Sql("db-a", 120m))],
            previous: [Rollup("2026-08-29", Sql("db-a", 80m), Sql("db-removed", 25m))]);

        var resource = overview.Resources.Single();
        resource.TotalCost.Should().Be(120m);
        resource.PreviousTotalCost.Should().Be(80m);
        Engine(overview, DatabaseResourceClassifier.SqlEngine).PreviousTotalCost.Should().Be(105m);
    }

    [Fact]
    public void Build_ShouldReportPreviousTotalAsNullWhenPreviousWindowHasNoData()
    {
        var overview = Build([Rollup("2026-09-01", Sql("db-a", 120m))], previous: []);

        overview.Resources.Single().PreviousTotalCost.Should().BeNull();
        Engine(overview, DatabaseResourceClassifier.SqlEngine).PreviousTotalCost.Should().BeNull();
    }

    [Fact]
    public void Build_ShouldComputeShareOfSubscriptionWhenAllocationCoversThePeriod()
    {
        var overview = Build(
            [
                Rollup("2026-09-01", allocation: 1000m, resources: Sql("db-a", 250m)),
                Rollup("2026-09-02", allocation: 1000m, resources: Sql("db-a", 250m))
            ],
            previous: []);

        var subscription = overview.Subscriptions.Single();
        subscription.SubscriptionTotalCost.Should().Be(2000m);
        subscription.ShareOfSubscriptionPercent.Should().Be(25m);
    }

    [Fact]
    public void Build_ShouldNotReportShareWhenAllocationIsIncomplete()
    {
        var overview = Build(
            [
                Rollup("2026-09-01", allocation: 1000m, resources: Sql("db-a", 250m)),
                Rollup("2026-09-02", resources: Sql("db-a", 250m))
            ],
            previous: []);

        var subscription = overview.Subscriptions.Single();
        subscription.TotalCost.Should().Be(500m);
        subscription.SubscriptionTotalCost.Should().BeNull();
        subscription.ShareOfSubscriptionPercent.Should().BeNull();
    }

    [Fact]
    public void Build_ShouldKeepOnlyTheDominantCurrency()
    {
        var usdRollup = Rollup("2026-09-02", Sql("db-usd", 90m));
        usdRollup.Currency = "USD";

        var overview = Build([Rollup("2026-09-01", Sql("db-brl", 300m)), usdRollup], previous: []);

        overview.Currency.Should().Be("BRL");
        overview.Resources.Should().ContainSingle(resource => resource.Label == "db-brl");
        Engine(overview, DatabaseResourceClassifier.SqlEngine).TotalCost.Should().Be(300m);
    }

    [Fact]
    public void Build_ShouldCountDaysMissingFromTheWindow()
    {
        // The range spans three days but only two rollups carry data. Without this the screen
        // silently sums less than the Azure portal for the same range.
        var overview = Build(
            [Rollup("2026-09-01", Sql("db-a", 10m)), Rollup("2026-09-02", Sql("db-a", 10m))],
            previous: []);

        overview.DaysWithoutData.Should().Be(1);
        overview.DaysWithData.Should().Be(2);
        overview.ExpectedDays.Should().Be(3);
        overview.Resources.Single().DaysWithCost.Should().Be(2);
    }

    [Fact]
    public void Build_ShouldKeepDailySeriesOrderedBySqlAndCosmos()
    {
        var overview = Build(
            [
                Rollup("2026-09-03", Cosmos("account-a", 20m)),
                Rollup("2026-09-01", Sql("db-a", 50m))
            ],
            previous: []);

        overview.Daily.Select(point => point.Date).Should().ContainInOrder("2026-09-01", "2026-09-03");
        overview.Daily.Single(point => point.Date == "2026-09-03").CosmosCost.Should().Be(20m);
        overview.Daily.Single(point => point.Date == "2026-09-01").SqlCost.Should().Be(50m);
    }

    [Fact]
    public void Build_ShouldReportGapsInThePreviousWindowThatFeedsTheVariation()
    {
        var overview = Build(
            [Rollup("2026-09-01", Sql("db-a", 5m)), Rollup("2026-09-02", Sql("db-a", 5m)), Rollup("2026-09-03", Sql("db-a", 5m))],
            previous: [Rollup("2026-08-29", Sql("db-a", 4m))]);

        overview.DaysWithoutData.Should().Be(0);
        overview.PreviousDaysWithoutData.Should().Be(2, "a variação compara períodos de tamanhos diferentes");
    }

    private static CostDatabaseOverview Build(
        IReadOnlyList<CostDatabaseDailyRollup> current,
        IReadOnlyList<CostDatabaseDailyRollup> previous)
        => DatabaseCostAggregator.Build(
            current,
            previous,
            new CostDatabaseRange
            {
                From = "2026-09-01",
                To = "2026-09-03",
                PreviousFrom = "2026-08-29",
                PreviousTo = "2026-08-31"
            });

    private static CostDatabaseDailyRollup Rollup(string date, params CostDatabaseRollupResource[] resources)
        => Rollup(date, null, resources);

    private static CostDatabaseDailyRollup Rollup(
        string date,
        decimal? allocation = null,
        params CostDatabaseRollupResource[] resources)
        => new()
        {
            Id = $"databaseCostRollup-{date}",
            Date = date,
            Currency = "BRL",
            Source = CostDataSources.CostManagementExport,
            DataAsOf = DateTime.Parse(date),
            HasCostData = true,
            Resources = resources.ToList(),
            SubscriptionTotals = allocation is null
                ? []
                : [new CostDatabaseRollupSubscription { SubscriptionId = "sub-001", SubscriptionTotalCost = allocation }]
        };

    private static CostDatabaseRollupResource Sql(string name, decimal cost)
        => Resource(name, "Microsoft.Sql/servers/databases", DatabaseResourceClassifier.SqlEngine, cost);

    private static CostDatabaseRollupResource Cosmos(string name, decimal cost)
        => Resource(name, "Microsoft.DocumentDB/databaseAccounts", DatabaseResourceClassifier.CosmosEngine, cost);

    private static CostDatabaseRollupResource Resource(string name, string resourceType, string engine, decimal cost)
        => new()
        {
            Label = name,
            ResourceId = $"/subscriptions/sub-001/resourceGroups/rg/providers/{resourceType}/{name}",
            ResourceType = resourceType,
            Engine = engine,
            ResourceGroup = "rg",
            SubscriptionId = "sub-001",
            TotalCost = cost
        };

    private static CostDatabaseEngineTotal Engine(CostDatabaseOverview overview, string engine)
        => overview.Engines.Single(item => item.Engine == engine);
}
