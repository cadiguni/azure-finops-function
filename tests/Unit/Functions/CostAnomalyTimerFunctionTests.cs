using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Functions;
using Personal.FinOpsApi.AzureFunctions.Models;
using System.Reflection;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Functions;

public class CostAnomalyTimerFunctionTests
{
    [Fact]
    public void IsReportUsableForRequestedDate_ShouldAllowLatestOlderReportButNotFutureReport()
    {
        var requestedDate = new DateTime(2026, 9, 16);

        CostAnomalyTimerFunction.IsReportUsableForRequestedDate(
                new CostAnomalyReport { Date = "2026-09-15" },
                requestedDate)
            .Should().BeTrue();
        CostAnomalyTimerFunction.IsReportUsableForRequestedDate(
                new CostAnomalyReport { Date = "2026-09-17" },
                requestedDate)
            .Should().BeFalse();
    }

    [Fact]
    public void ResolveBillingCycleReferenceDate_WhenIgnoringCurrentDay_ShouldUsePreviousDay()
    {
        var config = new CostAnomalyConfig
        {
            IgnoreCurrentDay = true
        };
        var requestedDate = new DateTime(2026, 7, 1);

        var result = InvokeResolveBillingCycleReferenceDate(config, requestedDate);

        result.Should().Be(new DateTime(2026, 6, 30));
    }

    [Fact]
    public void ResolveBillingCycleReferenceDate_WhenNotIgnoringCurrentDay_ShouldUseRequestedDate()
    {
        var config = new CostAnomalyConfig
        {
            IgnoreCurrentDay = false
        };
        var requestedDate = new DateTime(2026, 7, 1);

        var result = InvokeResolveBillingCycleReferenceDate(config, requestedDate);

        result.Should().Be(requestedDate);
    }

    [Fact]
    public void FinalizeReport_ShouldCalculateAnomaliesAndLatestAnalysisPeriod()
    {
        var config = new CostAnomalyConfig { BaselineDays = 3 };
        var report = new CostAnomalyReport
        {
            Subscriptions =
            [
                new SubscriptionCostAnomaly
                {
                    DataStatus = CostAnomalyDataStatus.Consolidated,
                    AnalysisDate = "2026-08-10",
                    HasAnomaly = true
                },
                new SubscriptionCostAnomaly
                {
                    DataStatus = CostAnomalyDataStatus.Consolidated,
                    AnalysisDate = "2026-08-11"
                },
                new SubscriptionCostAnomaly
                {
                    DataStatus = CostAnomalyDataStatus.QueryFailed
                }
            ]
        };

        InvokeFinalizeReport(report, config);

        report.TotalAnomaliesDetected.Should().Be(1);
        report.AnalysisDate.Should().Be("2026-08-11");
        report.BaselineStartDate.Should().Be("2026-08-08");
        report.BaselineEndDate.Should().Be("2026-08-10");
    }

    private static DateTime InvokeResolveBillingCycleReferenceDate(CostAnomalyConfig config, DateTime requestedDate)
    {
        var method = typeof(CostAnomalyTimerFunction).GetMethod(
            "ResolveBillingCycleReferenceDate",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        return (DateTime)method!.Invoke(null, new object[] { config, requestedDate })!;
    }

    private static void InvokeFinalizeReport(CostAnomalyReport report, CostAnomalyConfig config)
    {
        var method = typeof(CostAnomalyTimerFunction).GetMethod(
            "FinalizeReport",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        method!.Invoke(null, new object[] { report, config });
    }
}
