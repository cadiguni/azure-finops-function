using System.Text.Json;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Analyzers;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class DatabaseCostReviewTests
{
    [Theory]
    [InlineData(true, 300)]
    [InlineData(true, 0)]
    [InlineData(false, 300)]
    [InlineData(false, 0)]
    public void CreateFinding_ShouldRequireMetricsAndNeverInventSavings(bool sql, decimal monthlyCost)
    {
        var finding = Create(sql, new ResourceCostData(monthlyCost / 30, monthlyCost, "USD",
            CostSource: CostMatchSources.ResourceId, CostMatchConfidence: CostMatchConfidenceLevels.High,
            Provenance: new CostReadProvenance { Source = CostDataSources.CostManagementExport, ExportRunId = "run-1" },
            ObservedCost: monthlyCost / 2));
        finding.Type.Should().Be(sql ? FindingTypes.SQL_DATABASE_COST_REVIEW : FindingTypes.COSMOS_DB_COST_REVIEW);
        finding.EstimatedMonthlySavings.Should().Be(0);
        finding.EstimatedMonthlyCost.Should().Be(monthlyCost);
        finding.ObservedCost.Should().Be(monthlyCost / 2);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(finding));
        serialized.RootElement.GetProperty("costProvenance").GetProperty("exportRunId").GetString().Should().Be("run-1");
        finding.Description.Should().Contain("Investigação sugerida").And.Contain("USD");
        finding.Metadata["evidenceStatus"].Should().Be("InsufficientMetrics");
        finding.Metadata["savingsStatus"].Should().Be("NotEstimated");
        finding.Metadata["costAvailability"].Should().Be("Available");
        finding.Recommendation.Should().Contain("Economia ainda não estimada");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateFinding_WhenCostIsMissing_ShouldNotUseSkuOrFreeTierPriceGuesses(bool sql)
    {
        var finding = Create(sql, new ResourceCostData(0, 0));
        finding.EstimatedMonthlyCost.Should().Be(0);
        finding.DailyCost.Should().Be(0);
        finding.EstimatedMonthlySavings.Should().Be(0);
        finding.Description.Should().Contain("custo indisponível");
        finding.Metadata["costAvailability"].Should().Be("Missing");
    }

    [Fact]
    public void CosmosReview_ShouldNotRecommendReducingProvisionedThroughputForEveryAccount()
    {
        var finding = Create(false, new ResourceCostData(10, 300, CostSource: CostMatchSources.ResourceId));
        finding.Recommendation.Should().Contain("Em serverless, investigar consumo");
        finding.Recommendation.Should().Contain("em provisionado/autoscale, avaliar capacidade");
    }

    private static StandardFinding Create(bool sql, ResourceCostData cost)
    {
        using var document = JsonDocument.Parse("""
            {
              "resourceId": "/subscriptions/sub-001/resourceGroups/rg/providers/Microsoft.Sql/servers/s/databases/db",
              "name": "db", "resourceGroup": "rg", "location": "brazilsouth",
              "skuName": "P3", "skuTier": "Premium", "serviceObjective": "P3", "status": "Online",
              "kind": "GlobalDocumentDB", "offerType": "Standard", "enableFreeTier": "true"
            }
            """);
        return sql ? SqlDatabaseAnalyzer.CreateFinding("sub-001", document.RootElement, cost)
            : CosmosDatabaseAnalyzer.CreateFinding("sub-001", document.RootElement, cost);
    }
}
