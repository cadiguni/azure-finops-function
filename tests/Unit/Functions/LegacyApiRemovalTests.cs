using System.Reflection;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Functions;
using Microsoft.Azure.Functions.Worker;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Functions;

public sealed class LegacyApiRemovalTests
{
    [Fact]
    public void FunctionAssembly_ShouldExposeHealthButNotLegacyApis()
    {
        var functionNames = typeof(HealthFunction).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public))
            .Select(method => method.GetCustomAttribute<FunctionAttribute>()?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        functionNames.Should().Contain(new[]
        {
            "health", "ListTeams", "DailySummary", "PreGeneratedReportTimer",
            "BillingPeriodOverridesGet", "BillingPeriodOverrideUpsert", "BillingPeriodOverrideDelete",
            "OwnershipListTeams", "OwnershipGetTeam", "OwnershipAddSubscription",
            "OwnershipUpdateSubscription", "OwnershipDeleteSubscription", "OwnershipSyncFromManagementGroups"
        });
        functionNames.Should().NotContain(
        [
            "analyze-costs",
            "collect-costs",
            "analysis-status",
            "trigger-analysis",
            "force-consolidate",
            "ManualCostAnalysis",
            "debug-loganalytics",
            "GetFinOpsData",
            "AnalyzeDuplicateResources",
            "GetOwnershipDocuments",
            "UpsertOwnershipDocument",
            "CostByServiceManualRun",
            "CostByResourceManualRun",
            "ManualDailySummary",
            "ManualReportGeneration",
            "GetTeam",
            "UpsertTeam",
            "DeleteTeam",
            "ListTeamSubscriptions",
            "BillingPeriodCurrentGet"
        ]);
    }
}
