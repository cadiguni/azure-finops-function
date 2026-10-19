using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class ResourceCostResolverTests
{
    private readonly ResourceCostResolver _sut = new();

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-10, -5)]
    [InlineData(0.05, 0.03)]
    public void Resolve_ShouldIncludeAllObservedDaysRegardlessOfCost(decimal charge, decimal expectedDaily)
    {
        var rows = new[]
        {
            new CostDiagnosticRow { ResourceId = "resource", UsageDate = new DateTime(2026, 8, 1), TotalCost = charge },
            new CostDiagnosticRow { ResourceId = "resource", UsageDate = new DateTime(2026, 8, 2), TotalCost = 0 }
        };
        var result = _sut.Resolve(new ResourceCostResolveContext { ResourceId = "resource" }, rows,
            new DateTime(2026, 8, 1), new DateTime(2026, 8, 31));
        result.CostSource.Should().Be(CostMatchSources.ResourceId);
        result.BestMatch!.TotalCost.Should().Be(charge);
        result.BestMatch.DaysWithCost.Should().Be(2);
        result.DailyCost.Should().Be(expectedDaily);
    }

    [Fact]
    public void NormalizeResourceId_ShouldLowercaseAndTrimTrailingSlash()
    {
        var normalized = _sut.NormalizeResourceId("/Subscriptions/SUB-1/ResourceGroups/RG/providers/Microsoft.Sql/servers/sql1/databases/db1/");

        normalized.Should().Be("/subscriptions/sub-1/resourcegroups/rg/providers/microsoft.sql/servers/sql1/databases/db1");
    }

    [Fact]
    public void Resolve_WhenExactResourceIdMatch_ShouldUseHighConfidence()
    {
        var resourceId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.DocumentDB/databaseAccounts/cosmos1";
        var rows = new[]
        {
            new CostDiagnosticRow
            {
                ResourceId = resourceId,
                ResourceType = "Microsoft.DocumentDB/databaseAccounts",
                ResourceGroupName = "rg",
                ServiceName = "Azure Cosmos DB",
                MeterCategory = "Cosmos DB",
                MeterSubCategory = "Provisioned Throughput",
                Currency = "BRL",
                TotalCost = 300m,
                UsageDate = new DateTime(2026, 7, 1)
            }
        };

        var result = _sut.Resolve(
            new ResourceCostResolveContext
            {
                ResourceId = resourceId,
                ResourceType = "Microsoft.DocumentDB/databaseAccounts",
                ResourceGroup = "rg",
                SubscriptionId = "sub-1"
            },
            rows,
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 31));

        result.CostSource.Should().Be(CostMatchSources.ResourceId);
        result.CostMatchConfidence.Should().Be(CostMatchConfidenceLevels.High);
        result.CostMatchedResourceId.Should().Be(resourceId);
    }

    [Fact]
    public void Resolve_WhenCosmosLogicalChild_ShouldMatchParentAccount()
    {
        var accountId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.DocumentDB/databaseAccounts/cosmos1";
        var logicalDbId = $"{accountId}/sqlDatabases/appdb";
        var rows = new[]
        {
            new CostDiagnosticRow
            {
                ResourceId = accountId,
                ResourceType = "Microsoft.DocumentDB/databaseAccounts",
                ResourceGroupName = "rg",
                ServiceName = "Azure Cosmos DB",
                MeterCategory = "Cosmos DB",
                MeterSubCategory = "Provisioned Throughput",
                Currency = "BRL",
                TotalCost = 450m,
                UsageDate = new DateTime(2026, 7, 1)
            }
        };

        var result = _sut.Resolve(
            new ResourceCostResolveContext
            {
                ResourceId = logicalDbId,
                ResourceType = "Microsoft.DocumentDB/databaseAccounts/sqlDatabases",
                ResourceGroup = "rg",
                SubscriptionId = "sub-1"
            },
            rows,
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 31));

        result.CostSource.Should().Be(CostMatchSources.ParentResource);
        result.CostMatchConfidence.Should().Be(CostMatchConfidenceLevels.Medium);
        result.CostMatchedResourceId.Should().Be(accountId);
    }

    [Fact]
    public void Resolve_WhenSqlDatabaseInElasticPool_ShouldMatchElasticPoolParent()
    {
        var databaseId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Sql/servers/sqlsrv1/databases/appdb";
        var elasticPoolId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Sql/servers/sqlsrv1/elasticPools/pool1";
        var rows = new[]
        {
            new CostDiagnosticRow
            {
                ResourceId = elasticPoolId,
                ResourceType = "Microsoft.Sql/servers/elasticPools",
                ResourceGroupName = "rg",
                ServiceName = "SQL Database",
                MeterCategory = "SQL Database",
                MeterSubCategory = "Elastic Pool",
                Currency = "BRL",
                TotalCost = 900m,
                UsageDate = new DateTime(2026, 7, 2)
            }
        };

        var result = _sut.Resolve(
            new ResourceCostResolveContext
            {
                ResourceId = databaseId,
                ResourceType = "Microsoft.Sql/servers/databases",
                ResourceGroup = "rg",
                SubscriptionId = "sub-1",
                BillingParentResourceId = elasticPoolId
            },
            rows,
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 31));

        result.CostSource.Should().Be(CostMatchSources.ParentResource);
        result.CostMatchConfidence.Should().Be(CostMatchConfidenceLevels.Medium);
        result.CostMatchedResourceId.Should().Be(elasticPoolId);
    }

    [Fact]
    public void Resolve_WhenOnlyServiceMeterMatch_ShouldUseLowConfidence()
    {
        var resourceId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Sql/servers/sqlsrv1/databases/appdb";
        var rows = new[]
        {
            new CostDiagnosticRow
            {
                ResourceId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
                ResourceType = "Microsoft.Compute/virtualMachines",
                ResourceGroupName = "rg",
                ServiceName = "SQL Database",
                MeterCategory = "SQL Database",
                MeterSubCategory = "vCore",
                Currency = "BRL",
                TotalCost = 120m,
                UsageDate = new DateTime(2026, 7, 3)
            }
        };

        var result = _sut.Resolve(
            new ResourceCostResolveContext
            {
                ResourceId = resourceId,
                ResourceType = "Microsoft.Sql/servers/databases",
                ResourceGroup = "rg",
                SubscriptionId = "sub-1"
            },
            rows,
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 31));

        result.CostSource.Should().Be(CostMatchSources.NotMatched);
        result.CostMatchConfidence.Should().Be(CostMatchConfidenceLevels.Low);
        result.ServiceMeterMatches.Should().ContainSingle();
        result.BestMatch.Should().BeNull();
    }

    [Fact]
    public void Resolve_WhenNoMatches_ShouldReturnNotMatched()
    {
        var resourceId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.DocumentDB/databaseAccounts/cosmos1";
        var rows = new[]
        {
            new CostDiagnosticRow
            {
                ResourceId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
                ResourceType = "Microsoft.Compute/virtualMachines",
                ResourceGroupName = "rg",
                ServiceName = "Virtual Machines",
                MeterCategory = "Virtual Machines",
                MeterSubCategory = "D-Series",
                Currency = "BRL",
                TotalCost = 50m,
                UsageDate = new DateTime(2026, 7, 4)
            }
        };

        var result = _sut.Resolve(
            new ResourceCostResolveContext
            {
                ResourceId = resourceId,
                ResourceType = "Microsoft.DocumentDB/databaseAccounts",
                ResourceGroup = "rg-other",
                SubscriptionId = "sub-1"
            },
            rows,
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 31));

        result.CostSource.Should().Be(CostMatchSources.NotMatched);
        result.CostMatchConfidence.Should().Be(CostMatchConfidenceLevels.Low);
        result.CostNote.Should().Contain("Não foi possível associar custo");
    }
}
