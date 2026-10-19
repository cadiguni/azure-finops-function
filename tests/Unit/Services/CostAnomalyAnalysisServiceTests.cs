using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class CostAnomalyAnalysisServiceTests
{
    [Fact]
    public void Analyze_WhenBillingCycleWindowIsProvided_ShouldUseSubscriptionCycle()
    {
        var sut = new CostAnomalyAnalysisService(new NullLogger<CostAnomalyAnalysisService>());
        var requestedDate = new DateTime(2026, 6, 30);
        var dailyCosts = BuildDailyCosts(new DateTime(2026, 6, 12), requestedDate, 10m);
        var config = new CostAnomalyConfig
        {
            IgnoreCurrentDay = false,
            BaselineDays = 3,
            LookbackDays = 14
        };
        var billingWindow = new SubscriptionBillingCycleWindow
        {
            CycleStartDate = new DateTime(2026, 6, 15),
            CycleTurnDate = new DateTime(2026, 7, 15),
            Source = BillingPeriodSources.AzureBillingPeriodsApi,
            Confidence = BillingPeriodConfidence.High
        };

        var result = sut.Analyze("sub-1", "Sub 1", dailyCosts, requestedDate, config, billingWindow);

        result.CurrentCycleStartDate.Should().Be("2026-06-15");
        result.CurrentCycleTurnDate.Should().Be("2026-07-15");
        result.CurrentCycleRemainingDays.Should().Be(15);
        result.CurrentCycleSource.Should().Be(BillingPeriodSources.AzureBillingPeriodsApi);
        result.CurrentCycleConfidence.Should().Be(BillingPeriodConfidence.High);
        result.CurrentCycleCost.Should().Be(160m);
        result.CurrentCycleDailyAverage.Should().Be(10m);
        result.MonthlyProjection.Should().Be(310m);
        result.ProjectedOverBudget.Should().Be(result.MonthlyProjection - config.MonthlyBudget);
    }

    [Fact]
    public void Analyze_WhenBillingCycleWindowDoesNotContainAnalysisDate_ShouldFallbackToCalendarMonth()
    {
        var sut = new CostAnomalyAnalysisService(new NullLogger<CostAnomalyAnalysisService>());
        var requestedDate = new DateTime(2026, 6, 30);
        var dailyCosts = BuildDailyCosts(new DateTime(2026, 6, 10), requestedDate, 12m);
        var config = new CostAnomalyConfig
        {
            IgnoreCurrentDay = false,
            BaselineDays = 3,
            LookbackDays = 14
        };
        var invalidWindow = new SubscriptionBillingCycleWindow
        {
            CycleStartDate = new DateTime(2026, 7, 15),
            CycleTurnDate = new DateTime(2026, 8, 15),
            Source = BillingPeriodSources.AzureBillingPeriodsApi,
            Confidence = BillingPeriodConfidence.High
        };

        var result = sut.Analyze("sub-1", "Sub 1", dailyCosts, requestedDate, config, invalidWindow);

        result.CurrentCycleStartDate.Should().Be("2026-06-01");
        result.CurrentCycleTurnDate.Should().Be("2026-07-01");
        result.CurrentCycleSource.Should().Be(BillingPeriodSources.DefaultCalendarMonth);
    }

    [Fact]
    public void Analyze_WhenBaselineIsIncomplete_ShouldStillCalculateCrossMonthBillingCycleCost()
    {
        var sut = new CostAnomalyAnalysisService(new NullLogger<CostAnomalyAnalysisService>());
        var requestedDate = new DateTime(2026, 9, 2);
        var dailyCosts = new List<DailyCostEntry>
        {
            new()
            {
                Date = "2026-08-20", TotalCost = 10m, Currency = "BRL",
                UsedFallback = true, FallbackReason = "Watermark do Export está defasado."
            },
            new() { Date = "2026-09-01", TotalCost = 20m, Currency = "BRL" }
        };
        var billingWindow = new SubscriptionBillingCycleWindow
        {
            CycleStartDate = new DateTime(2026, 8, 15),
            CycleTurnDate = new DateTime(2026, 9, 15),
            Source = BillingPeriodSources.AzureBillingPeriodsApi,
            Confidence = BillingPeriodConfidence.High
        };

        var result = sut.Analyze(
            "sub-1",
            "Sub 1",
            dailyCosts,
            requestedDate,
            new CostAnomalyConfig { IgnoreCurrentDay = true, BaselineDays = 3 },
            billingWindow);

        result.DataStatus.Should().Be(CostAnomalyDataStatus.InsufficientData);
        result.AnalysisDate.Should().Be("2026-09-01");
        result.CurrentCycleStartDate.Should().Be("2026-08-15");
        result.CurrentCycleTurnDate.Should().Be("2026-09-15");
        result.CurrentCycleCost.Should().Be(30m);
        result.CurrentCycleSource.Should().Be(BillingPeriodSources.AzureBillingPeriodsApi);
        result.CostUsedFallback.Should().BeTrue();
        result.CostFallbackReason.Should().Contain("defasado");
    }

    [Fact]
    public void Analyze_WhenCostsAreVeryLow_ShouldAvoidExplosivePercentageVariation()
    {
        var sut = new CostAnomalyAnalysisService(new NullLogger<CostAnomalyAnalysisService>());
        var requestedDate = new DateTime(2026, 8, 6);
        var config = new CostAnomalyConfig
        {
            IgnoreCurrentDay = false,
            BaselineDays = 3,
            LookbackDays = 7,
            MinimumIncreaseAmount = 10m
        };

        var dailyCosts = new List<DailyCostEntry>
        {
            new() { Date = "2026-08-03", TotalCost = 0.00m, Currency = "BRL" },
            new() { Date = "2026-08-04", TotalCost = 0.00m, Currency = "BRL" },
            new() { Date = "2026-08-05", TotalCost = 0.00m, Currency = "BRL" },
            new() { Date = "2026-08-06", TotalCost = 0.02m, Currency = "BRL" }
        };

        var result = sut.Analyze("sub-low", "Sub Low", dailyCosts, requestedDate, config);

        result.BaselineAverage.Should().Be(0m);
        result.LatestDailyCost.Should().Be(0.02m);
        result.IncreaseAmount.Should().Be(0.02m);
        result.IncreasePercent.Should().Be(0m);
        result.HasAnomaly.Should().BeFalse();
    }

    [Fact]
    public void Analyze_WhenDailyCostsComeFromExport_ShouldExposeSourceAndDataAsOf()
    {
        var sut = new CostAnomalyAnalysisService(new NullLogger<CostAnomalyAnalysisService>());
        var dailyCosts = BuildDailyCosts(
            new DateTime(2026, 8, 15),
            new DateTime(2026, 8, 18),
            10m);
        dailyCosts.ForEach(entry => entry.Source = CostDataSources.CostManagementExport);

        var result = sut.Analyze(
            "sub-export",
            "Export subscription",
            dailyCosts,
            new DateTime(2026, 8, 18),
            new CostAnomalyConfig { IgnoreCurrentDay = false, BaselineDays = 3 });

        result.CostSource.Should().Be(CostDataSources.CostManagementExport);
        result.CostDataAsOf.Should().Be("2026-08-18");
    }

    private static List<DailyCostEntry> BuildDailyCosts(DateTime startDate, DateTime endDate, decimal amount)
    {
        var costs = new List<DailyCostEntry>();
        for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
        {
            costs.Add(new DailyCostEntry
            {
                Date = date.ToString("yyyy-MM-dd"),
                TotalCost = amount,
                Currency = "BRL"
            });
        }

        return costs;
    }
}
