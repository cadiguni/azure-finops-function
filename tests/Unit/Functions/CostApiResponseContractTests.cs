using System.Text.Json;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Functions;

/// <summary>
/// O portal lê estas respostas por nome exato. O worker isolated não aplica camelCase sozinho, e o
/// helper de asserção das outras suítes compara nomes ignorando maiúsculas — então o contrato
/// precisa de uma verificação sensível a caixa, senão um PascalCase volta sem ninguém notar.
/// </summary>
public sealed class CostApiResponseContractTests
{
    // Opções deliberadamente padrão: sem política de nomes, para provar que os nomes vêm dos atributos.
    private static readonly JsonSerializerOptions PascalCaseDefault = new();

    [Fact]
    public void CostApiResponse_ShouldExposeCamelCaseNames()
    {
        var payload = Serialize(new CostApiResponse<CostByServiceTrendRow>
        {
            DataAsOf = new DateTime(2026, 9, 8),
            Source = CostDataSources.CostManagementExport,
            Items = [new CostByServiceTrendRow { Date = "2026-09-08", TotalCost = 10m, Currency = "BRL" }]
        });

        payload.Should().ContainKeys(
            "dataAsOf", "source", "items", "allocation", "sourceByDate", "availableDates", "missingDates");
        payload.Should().NotContainKeys(
            "DataAsOf", "Source", "Items", "Allocation", "SourceByDate", "AvailableDates", "MissingDates");
    }

    [Fact]
    public void CostByServiceTrendRow_ShouldExposeCamelCaseNames()
    {
        Serialize(new CostByServiceTrendRow { Date = "2026-09-08", TotalCost = 10m, Currency = "BRL" })
            .Should().ContainKeys("date", "totalCost", "currency");
    }

    [Fact]
    public void CostByServiceRow_ShouldExposeCamelCaseNames()
    {
        Serialize(new CostByServiceRow { Label = "Azure SQL", TotalCost = 10m })
            .Should().ContainKeys("label", "totalCost", "currency", "count", "subscriptionId");
    }

    [Fact]
    public void CostByResourceRow_ShouldExposeCamelCaseNames()
    {
        Serialize(new CostByResourceRow { Label = "db-a", ResourceId = "/subscriptions/sub/db-a" })
            .Should().ContainKeys(
                "label", "resourceId", "serviceName", "resourceGroup", "resourceLocation",
                "resourceType", "tags", "totalCost", "currency", "count", "subscriptionId");
    }

    [Fact]
    public void CostAllocationSummary_ShouldExposeCamelCaseNames()
    {
        Serialize(new CostAllocationSummary { SubscriptionId = "sub-001", TotalCost = 100m })
            .Should().ContainKeys(
                "source", "subscriptionId", "usageDate", "currency",
                "totalCost", "allocatedResourceCost", "unallocatedCost");
    }

    [Fact]
    public void CostDatabaseOverview_ShouldExposeCamelCaseNames()
    {
        var payload = Serialize(new CostDatabaseOverview
        {
            Range = new CostDatabaseRange { From = "2026-09-01", To = "2026-09-08" }
        });

        payload.Should().ContainKeys(
            "range", "dataAsOf", "source", "currency", "daysWithoutData",
            "engines", "subscriptions", "resources", "daily");
        payload.Should().NotContainKeys("Range", "Engines", "Subscriptions", "Resources", "Daily");
    }

    [Fact]
    public void CostDatabaseNestedModels_ShouldExposeCamelCaseNames()
    {
        Serialize(new CostDatabaseRange())
            .Should().ContainKeys("from", "to", "previousFrom", "previousTo");
        Serialize(new CostDatabaseEngineTotal())
            .Should().ContainKeys("engine", "totalCost", "previousTotalCost", "resourceCount");
        Serialize(new CostDatabaseSubscriptionTotal())
            .Should().ContainKeys(
                "subscriptionId", "totalCost", "sqlCost", "cosmosCost",
                "subscriptionTotalCost", "shareOfSubscriptionPercent", "resourceCount");
        Serialize(new CostDatabaseResourceTotal())
            .Should().ContainKeys(
                "resourceId", "label", "engine", "resourceGroup", "resourceType",
                "subscriptionId", "totalCost", "previousTotalCost", "daysWithCost");
        Serialize(new CostDatabaseDailyPoint())
            .Should().ContainKeys("date", "sqlCost", "cosmosCost");
    }

    [Fact]
    public void CostAllocationCoverage_ShouldExposeCamelCaseNames()
    {
        Serialize(new CostAllocationCoverageSummary { From = "2026-09-01", To = "2026-09-08" })
            .Should().ContainKeys(
                "from", "to", "currency", "tagName", "daysWithoutData",
                "taggedCost", "untaggedCost", "coveragePercent", "subscriptions");

        Serialize(new CostAllocationCoverageSubscriptionTotal())
            .Should().ContainKeys(
                "subscriptionId", "totalCost", "resourceCost", "unallocatedCost",
                "taggedCost", "untaggedCost", "costCenters");

        Serialize(new CostAllocationCoverageCostCenter())
            .Should().ContainKeys("value", "totalCost", "resourceCount");
    }

    private static Dictionary<string, JsonElement> Serialize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, PascalCaseDefault);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }
}
