using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class FinOpsResultAggregatorTests
{
    [Fact]
    public void BuildSummary_WhenRecommendationsAreEmpty_ShouldReturnZeroedSummary()
    {
        var summary = FinOpsResultAggregator.BuildSummary(new List<CostRecommendation>());

        summary.TotalRecommendations.Should().Be(0);
        summary.TotalEstimatedMonthlySavings.Should().Be(0);
        summary.ByType.Should().BeEmpty();
    }

    [Fact]
    public void BuildSummary_WhenRecommendationsExist_ShouldAggregateByType()
    {
        var recommendations = new List<CostRecommendation>
        {
            new() { Type = "IdleVirtualMachine", EstimatedMonthlySavings = 100 },
            new() { Type = "IdleVirtualMachine", EstimatedMonthlySavings = 50 },
            new() { Type = "UnusedPublicIP", EstimatedMonthlySavings = 20 }
        };

        var summary = FinOpsResultAggregator.BuildSummary(recommendations);

        summary.TotalRecommendations.Should().Be(3);
        summary.TotalEstimatedMonthlySavings.Should().Be(170);
        summary.ByType.Should().ContainKey("IdleVirtualMachine");
        summary.ByType["IdleVirtualMachine"].Count.Should().Be(2);
        summary.ByType["IdleVirtualMachine"].EstimatedMonthlySavings.Should().Be(150);
        summary.ByType["UnusedPublicIP"].Count.Should().Be(1);
        summary.ByType["UnusedPublicIP"].EstimatedMonthlySavings.Should().Be(20);
    }

    [Fact]
    public void BuildSummary_ShouldUsePotentialMonthlySavingsAlias()
    {
        var recommendations = new List<CostRecommendation>
        {
            new() { Type = "UnderUtilizedStorageAccount", PotentialMonthlySavings = 77.5m }
        };

        var summary = FinOpsResultAggregator.BuildSummary(recommendations);

        summary.TotalEstimatedMonthlySavings.Should().Be(77.5m);
        summary.ByType["UnderUtilizedStorageAccount"].EstimatedMonthlySavings.Should().Be(77.5m);
    }
}
