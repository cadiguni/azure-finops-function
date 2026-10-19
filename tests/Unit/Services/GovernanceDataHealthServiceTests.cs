using Azure;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class GovernanceDataHealthServiceTests
{
    private const string Subscription = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly Mock<ICostExportVersionStore> _versions = new();
    private readonly Mock<ICostStorageRepository> _storage = new();

    [Fact]
    public async Task MissingIndex_IsUnknown_NotZeroOrSuccessful()
    {
        var result = await Read();
        result.Status.Should().Be("unknown");
        result.CoveragePercent.Should().BeNull();
        result.MissingDates.Should().BeNull();
        result.IsStale.Should().BeNull();
        result.LastImportedAt.Should().BeNull();
        result.HistoricalFailureCount.Should().BeNull();
        result.LastCollectorRunAt.Should().BeNull();
        result.QueueStatus.Should().Be("unavailable");
        result.TimerStatus.Should().Be("unavailable");
    }

    [Fact]
    public async Task WatermarkWithoutIndex_DoesNotProveCoverageOrNoFailures()
    {
        _storage.Setup(s => s.LoadPublicationWatermarkAsync(Subscription, "Shadow", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportPublicationWatermark { MinUsageDate = new(2026, 9, 1), MaxUsageDate = new(2026, 9, 8), ProcessedAt = Now });
        var result = await Read();
        result.CoveragePercent.Should().BeNull();
        result.Status.Should().Be("unknown");
        result.LatestUsageDate.Should().Be(new DateOnly(2026, 9, 8));
        result.LastImportedAt.Should().Be(Now);
        result.HistoricalFailureCount.Should().BeNull();
    }

    [Fact]
    public async Task GapsAndFreshness_AreDerivedFromPublishedDays()
    {
        SetIndex(1, 3);
        var result = await Read();
        result.CoveragePercent.Should().Be(25m);
        result.MissingDates.Should().Contain("2026-09-02").And.HaveCount(6);
        result.IsStale.Should().BeTrue();
        result.Status.Should().Be("incomplete");
    }

    [Fact]
    public async Task FullWindow_IsCoveredButCollectorStateIsStillUnknown()
    {
        SetIndex(Enumerable.Range(1, 8).ToArray());
        var result = await Read();
        result.Status.Should().Be("covered");
        result.CoveragePercent.Should().Be(100m);
        result.IsStale.Should().BeFalse();
        result.LastCollectorRunAt.Should().BeNull();
        result.Warnings.Should().Contain(w => w.Contains("Shadow"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadFailure_IsUnavailable_NotHealthy(bool indexFailure)
    {
        SetIndex(Enumerable.Range(1, 8).ToArray());
        if (indexFailure) _versions.Setup(v => v.LoadIndexAsync(Subscription, "Shadow", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "Forbidden"));
        else _storage.Setup(s => s.LoadPublicationWatermarkAsync(Subscription, "Shadow", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "Unavailable"));
        var result = await Read();
        result.Status.Should().Be("unavailable");
    }

    [Fact]
    public async Task EmptyPublishedIndex_IsZeroCoverageButNotZeroCost()
    {
        SetIndex();
        var result = await Read();
        result.CoveragePercent.Should().Be(0);
        result.MissingDates.Should().HaveCount(8);
        result.LatestUsageDate.Should().BeNull();
    }

    [Fact]
    public async Task OversizedWindow_IsRejectedBeforeReadingStorage()
    {
        await FluentActions.Invoking(() => Create().GetAsync(Subscription, new(2026, 8, 1), new(2026, 9, 8), Now))
            .Should().ThrowAsync<ArgumentException>();
        _versions.VerifyNoOtherCalls();
        _storage.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Publishing")]
    [InlineData("Partial")]
    public async Task IncompletePublicationIsDegradedEvenWithFullDateCoverage(string publicationStatus)
    {
        SetIndex(Enumerable.Range(1, 8).ToArray());
        _storage.Setup(s => s.LoadPublicationWatermarkAsync(Subscription, "Shadow", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportPublicationWatermark { Status = publicationStatus, ProcessedAt = Now });
        var result = await Read();
        result.CoveragePercent.Should().Be(100m);
        result.Status.Should().Be("degraded");
        result.LastPublicationStatus.Should().Be(publicationStatus);
        result.HistoricalFailureCount.Should().BeNull();
    }

    [Fact]
    public async Task DatesOutsideRequestedWindowDoNotInflateCoverage()
    {
        SetIndex(1, 8, 9, 10);
        var result = await Read();
        result.CoveragePercent.Should().Be(25m);
        result.AvailableDates.Should().Equal("2026-09-01", "2026-09-08");
        result.MissingDates.Should().HaveCount(6);
        result.Status.Should().Be("incomplete");
    }

    [Fact]
    public async Task MaximumWindowIsInclusiveAcrossMonthBoundary()
    {
        var from = new DateOnly(2026, 8, 15);
        var to = from.AddDays(30);
        var days = Enumerable.Range(0, 31).ToDictionary(i => from.AddDays(i).ToString("yyyy-MM-dd"),
            _ => new CostExportDayReference { ImportedAt = Now });
        _versions.Setup(v => v.LoadIndexAsync(Subscription, "Shadow", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportRevisionIndex { Days = new(days) });
        var result = await Create().GetAsync(Subscription, from, to, Now);
        result.AvailableDates.Should().HaveCount(31).And.Contain("2026-08-31").And.Contain("2026-09-01");
        result.MissingDates.Should().BeEmpty();
        result.CoveragePercent.Should().Be(100m);
    }

    [Fact]
    public async Task CancellationPropagatesWithoutReadingRemainingEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        _versions.Setup(v => v.LoadIndexAsync(Subscription, "Shadow", cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        await FluentActions.Awaiting(() => Create().GetAsync(Subscription, new(2026, 9, 1), new(2026, 9, 8), Now, cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        _storage.VerifyNoOtherCalls();
    }

    private Task<DataHealthResponse> Read() => Create().GetAsync(Subscription, new(2026, 9, 1), new(2026, 9, 8), Now);
    private GovernanceDataHealthService Create() => new(_versions.Object, _storage.Object,
        new ConfigurationBuilder().Build(), NullLogger<GovernanceDataHealthService>.Instance);
    private void SetIndex(params int[] days) => _versions.Setup(v => v.LoadIndexAsync(Subscription, "Shadow", It.IsAny<CancellationToken>()))
        .ReturnsAsync(new CostExportRevisionIndex { Days = new(days.ToDictionary(d => $"2026-09-{d:00}",
            _ => new CostExportDayReference { ImportedAt = Now })) });
}
