using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class DatabaseCostRollupServiceTests
{
    private static readonly DateTime Date = new(2026, 9, 8);

    [Fact]
    public void BuildRollup_ShouldKeepOnlyDatabaseResources()
    {
        var rollup = DatabaseCostRollupService.BuildRollup(
            Date,
            [Sql("db-a", 100m), Cosmos("account-a", 40m), Row("vm-a", "Microsoft.Compute/virtualMachines", 900m)],
            []);

        rollup.Resources.Select(resource => resource.Label).Should().BeEquivalentTo("db-a", "account-a");
        rollup.Resources.Single(resource => resource.Label == "db-a").Engine
            .Should().Be(DatabaseResourceClassifier.SqlEngine);
        rollup.Resources.Single(resource => resource.Label == "account-a").Engine
            .Should().Be(DatabaseResourceClassifier.CosmosEngine);
    }

    [Fact]
    public void BuildRollup_ShouldClassifyCanonicalServiceNamesWhenResourceTypeIsUnavailable()
    {
        var sql = Row("sql-cost", string.Empty, 100m);
        sql.ServiceName = "Azure SQL Database";
        var cosmos = Row("cosmos-cost", string.Empty, 40m);
        cosmos.ServiceName = "Azure Cosmos DB";

        var rollup = DatabaseCostRollupService.BuildRollup(Date, [sql, cosmos], []);

        rollup.Resources.Should().HaveCount(2);
        rollup.Resources.Select(resource => resource.Engine)
            .Should().BeEquivalentTo(DatabaseResourceClassifier.SqlEngine, DatabaseResourceClassifier.CosmosEngine);
    }

    [Fact]
    public void BuildRollup_ShouldSumRepeatedRowsOfTheSameResource()
    {
        var rollup = DatabaseCostRollupService.BuildRollup(Date, [Sql("db-a", 60m), Sql("db-a", 40m)], []);

        rollup.Resources.Single().TotalCost.Should().Be(100m);
    }

    [Fact]
    public void BuildRollup_ShouldKeepOnlyTheDominantCurrency()
    {
        var usd = Sql("db-usd", 80m);
        usd.Currency = "USD";

        var rollup = DatabaseCostRollupService.BuildRollup(Date, [Sql("db-brl", 300m), usd], []);

        rollup.Currency.Should().Be("BRL");
        rollup.Resources.Should().ContainSingle(resource => resource.Label == "db-brl");
    }

    [Fact]
    public void BuildRollup_ShouldFlagDayWithoutCostData()
    {
        var rollup = DatabaseCostRollupService.BuildRollup(Date, [], []);

        rollup.HasCostData.Should().BeFalse();
        rollup.DataAsOf.Should().BeNull();
        rollup.Resources.Should().BeEmpty();
    }

    [Fact]
    public void BuildRollup_ShouldKeepSubscriptionTotalsFromAllocation()
    {
        var rollup = DatabaseCostRollupService.BuildRollup(
            Date,
            [Sql("db-a", 100m)],
            [new CostAllocationSummary
            {
                SubscriptionId = "sub-001",
                UsageDate = Date,
                Currency = "BRL",
                TotalCost = 900m
            }]);

        rollup.SubscriptionTotals.Single().SubscriptionTotalCost.Should().Be(900m);
    }

    [Fact]
    public void BuildRollup_ShouldUseDeterministicIdPerDay()
    {
        DatabaseCostRollupService.BuildRollup(Date, [Sql("db-a", 10m)], []).Id
            .Should().Be(DatabaseCostRollupService.BuildRollup(Date, [], []).Id);
    }

    private static CostByResourceRow Sql(string name, decimal cost)
        => Row(name, "Microsoft.Sql/servers/databases", cost);

    private static CostByResourceRow Cosmos(string name, decimal cost)
        => Row(name, "Microsoft.DocumentDB/databaseAccounts", cost);

    private static CostByResourceRow Row(string name, string resourceType, decimal cost) => new()
    {
        Label = name,
        ResourceId = $"/subscriptions/sub-001/resourceGroups/rg/providers/{resourceType}/{name}",
        ResourceType = resourceType,
        ResourceGroup = "rg",
        SubscriptionId = "sub-001",
        Currency = "BRL",
        TotalCost = cost
    };
}
