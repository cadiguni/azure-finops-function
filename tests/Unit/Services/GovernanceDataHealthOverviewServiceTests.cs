using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class GovernanceDataHealthOverviewServiceTests
{
    private const string Covered = "11111111-1111-1111-1111-111111111111";
    private const string Missing = "22222222-2222-2222-2222-222222222222";
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly Mock<ICostExportVersionStore> _versions = new();
    private readonly Mock<ICostStorageRepository> _storage = new();
    private readonly Mock<ICostAnomalyReportRepository> _reports = new();

    [Fact]
    public async Task ListsEverySubscriptionFromLatestAnomalyReport_ProblemsFirst()
    {
        _reports.Setup(r => r.LoadLatestReportAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CostAnomalyReport
        {
            Date = "2026-10-14",
            Subscriptions =
            [
                new SubscriptionCostAnomaly { SubscriptionId = Covered, SubscriptionName = "Covered sub" },
                new SubscriptionCostAnomaly { SubscriptionId = Missing, SubscriptionName = "Missing sub" },
                new SubscriptionCostAnomaly { SubscriptionId = Covered.ToUpperInvariant(), SubscriptionName = "duplicate" }
            ]
        });
        // Delay 2 days → window 2026-09-30..2026-10-13 (14 days).
        var days = Enumerable.Range(0, 14).ToDictionary(i => new DateOnly(2026, 9, 30).AddDays(i).ToString("yyyy-MM-dd"),
            _ => new CostExportDayReference { ImportedAt = Now });
        _versions.Setup(v => v.LoadIndexAsync(Covered, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportRevisionIndex { Days = new(days) });

        var result = await Create().GetAsync(Now);

        result.From.Should().Be(new DateOnly(2026, 9, 30));
        result.To.Should().Be(new DateOnly(2026, 10, 13));
        result.SubscriptionSource.Should().Be("cost-anomaly-report:2026-10-14");
        result.Subscriptions.Select(row => row.SubscriptionId).Should().Equal(Missing, Covered);
        result.Subscriptions[0].Status.Should().Be("unknown");
        result.Subscriptions[0].CoveragePercent.Should().BeNull();
        result.Subscriptions[1].Status.Should().Be("covered");
        result.Subscriptions[1].SubscriptionName.Should().Be("Covered sub");
        result.Subscriptions[1].CoveragePercent.Should().Be(100m);
        result.Subscriptions[1].MissingDays.Should().Be(0);
    }

    [Fact]
    public async Task UnexpectedFailureOnOneSubscription_IsUnavailableRowNotWholeFailure()
    {
        _reports.Setup(r => r.LoadLatestReportAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CostAnomalyReport
        {
            Subscriptions = [new SubscriptionCostAnomaly { SubscriptionId = Covered }, new SubscriptionCostAnomaly { SubscriptionId = Missing }]
        });
        _versions.Setup(v => v.LoadIndexAsync(Covered, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await Create().GetAsync(Now);

        result.Subscriptions.Should().HaveCount(2);
        result.Subscriptions.Single(row => row.SubscriptionId == Covered).Status.Should().Be("unavailable");
    }

    [Fact]
    public async Task WithoutAnomalyReport_FallsBackToIngestionAllowlistAndWarns()
    {
        _reports.Setup(r => r.LoadLatestReportAsync(It.IsAny<CancellationToken>())).ReturnsAsync((CostAnomalyReport?)null);

        var result = await Create(new() { ["EnableCostExportImport"] = "true", ["COST_EXPORT_SUBSCRIPTIONS"] = Missing })
            .GetAsync(Now);

        result.SubscriptionSource.Should().Be("COST_EXPORT_SUBSCRIPTIONS");
        result.Subscriptions.Should().ContainSingle(row => row.SubscriptionId == Missing && row.ExportIngestion);
        result.Warnings.Should().ContainSingle();
    }

    private GovernanceDataHealthOverviewService Create(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new()).Build();
        var health = new GovernanceDataHealthService(_versions.Object, _storage.Object, configuration,
            NullLogger<GovernanceDataHealthService>.Instance);
        return new GovernanceDataHealthOverviewService(health, _reports.Object, configuration,
            NullLogger<GovernanceDataHealthOverviewService>.Instance);
    }
}
