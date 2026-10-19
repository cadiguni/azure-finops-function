using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public sealed class CostCollectionRoutingTests
{
    [Fact]
    public void Defaults_ShouldKeepLegacyCollectorsDisabledAndSubscriptionsUnmigrated()
    {
        var configuration = BuildConfiguration([]);

        CostCollectionRouting.IsLegacyByServiceEnabled(configuration).Should().BeFalse();
        CostCollectionRouting.IsLegacyByResourceEnabled(configuration).Should().BeFalse();
        CostCollectionRouting.IsCutoverSubscription(configuration, "sub-1").Should().BeFalse();
        CostCollectionRouting.FilterLegacySubscriptions(configuration, ["sub-1"])
            .Should().Equal("sub-1");
    }

    [Fact]
    public void IngestionAllowlist_WithoutCutover_ShouldKeepSubscriptionsInLegacyCollectors()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["EnableCostExportImport"] = "true",
            ["COST_EXPORT_SUBSCRIPTIONS"] = "sub-export"
        });

        CostCollectionRouting.FilterLegacySubscriptions(
                configuration,
                ["sub-export", "sub-legacy"])
            .Should().Equal("sub-export", "sub-legacy");
        CostCollectionRouting.IsExportIngestionSubscription(configuration, "sub-export")
            .Should().BeTrue();
        CostCollectionRouting.IsExportIngestionSubscription(configuration, "sub-legacy")
            .Should().BeFalse();
    }

    [Fact]
    public void ImportAllSubscriptions_WhenEnabled_ShouldUseExportForAnySubscriptionWithoutCutover()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["EnableCostExportImport"] = "true",
            ["COST_EXPORT_IMPORT_ALL_SUBSCRIPTIONS"] = "true",
            ["COST_EXPORT_SUBSCRIPTIONS"] = "",
            ["COST_EXPORT_CUTOVER_SUBSCRIPTIONS"] = ""
        });

        CostCollectionRouting.IsExportIngestionSubscription(configuration, "sub-any")
            .Should().BeTrue();
        CostCollectionRouting.IsCutoverSubscription(configuration, "sub-any")
            .Should().BeFalse();
        CostCollectionRouting.FilterLegacySubscriptions(configuration, ["sub-any"])
            .Should().Equal("sub-any");
    }

    [Fact]
    public void ValidateExportConfiguration_WhenImportAllIsEnabled_ShouldAcceptExplicitCutover()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["EnableCostExportImport"] = "true",
            ["COST_EXPORT_IMPORT_ALL_SUBSCRIPTIONS"] = "true",
            ["COST_EXPORT_SUBSCRIPTIONS"] = "",
            ["COST_EXPORT_CUTOVER_SUBSCRIPTIONS"] = "sub-cutover"
        });

        FluentActions.Invoking(() => CostCollectionRouting.ValidateExportConfiguration(configuration))
            .Should().NotThrow();
        CostCollectionRouting.IsCutoverSubscription(configuration, "sub-cutover")
            .Should().BeTrue();
    }

    [Fact]
    public void CutoverAllowlist_ShouldExcludeOnlyExplicitSubscriptionsFromLegacyCollectors()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["EnableCostExportImport"] = "true",
            ["COST_EXPORT_SUBSCRIPTIONS"] = "sub-shadow,sub-cutover",
            ["COST_EXPORT_CUTOVER_SUBSCRIPTIONS"] = "sub-cutover"
        });

        CostCollectionRouting.FilterLegacySubscriptions(
                configuration,
                ["sub-shadow", "sub-cutover", "sub-legacy"])
            .Should().Equal("sub-shadow", "sub-legacy");
    }

    [Theory]
    [InlineData("false", "sub-cutover", "sub-cutover")]
    [InlineData("true", "", "sub-cutover")]
    [InlineData("true", "sub-shadow", "sub-cutover")]
    public void IsCutoverSubscription_WhenPrerequisiteIsMissing_ShouldKeepLegacy(
        string importEnabled,
        string ingestion,
        string cutover)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["EnableCostExportImport"] = importEnabled,
            ["COST_EXPORT_SUBSCRIPTIONS"] = ingestion,
            ["COST_EXPORT_CUTOVER_SUBSCRIPTIONS"] = cutover
        });

        CostCollectionRouting.IsCutoverSubscription(configuration, "sub-cutover").Should().BeFalse();
        CostCollectionRouting.FilterLegacySubscriptions(configuration, ["sub-cutover"])
            .Should().Equal("sub-cutover");
    }

    [Fact]
    public void ValidateExportConfiguration_WhenCutoverIsConfiguredAndImportDisabled_ShouldReject()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["EnableCostExportImport"] = "false",
            ["COST_EXPORT_SUBSCRIPTIONS"] = "sub-cutover",
            ["COST_EXPORT_CUTOVER_SUBSCRIPTIONS"] = "sub-cutover"
        });

        FluentActions.Invoking(() => CostCollectionRouting.ValidateExportConfiguration(configuration))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*EnableCostExportImport=true*");
    }

    [Fact]
    public void ValidateExportConfiguration_WhenCutoverIsOutsideIngestion_ShouldReject()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["EnableCostExportImport"] = "true",
            ["COST_EXPORT_SUBSCRIPTIONS"] = "sub-shadow",
            ["COST_EXPORT_CUTOVER_SUBSCRIPTIONS"] = "sub-cutover"
        });

        FluentActions.Invoking(() => CostCollectionRouting.ValidateExportConfiguration(configuration))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*subconjunto*");
    }

    [Fact]
    public void EmptyCutoverAllowlist_ShouldNeverExcludeAllSubscriptions()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["EnableCostExportImport"] = "true",
            ["COST_EXPORT_SUBSCRIPTIONS"] = "sub-one,sub-two",
            ["COST_EXPORT_CUTOVER_SUBSCRIPTIONS"] = ""
        });

        CostCollectionRouting.IsCutoverSubscription(configuration, "sub-one").Should().BeFalse();
        CostCollectionRouting.FilterLegacySubscriptions(configuration, ["sub-one", "sub-two"])
            .Should().Equal("sub-one", "sub-two");
    }

    [Fact]
    public void LegacyCollectorFlags_ShouldBeIndependent()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["EnableLegacyCostByServiceCollection"] = "false",
            ["EnableLegacyCostByResourceCollection"] = "true"
        });

        CostCollectionRouting.IsLegacyByServiceEnabled(configuration).Should().BeFalse();
        CostCollectionRouting.IsLegacyByResourceEnabled(configuration).Should().BeTrue();
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
