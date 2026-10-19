using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public sealed class CostStorageRepositoryConcurrencyTests
{
    [Fact]
    public async Task CompareRuns_WhenConcurrentOlderExecutionCompletesLast_ShouldKeepNewerPointer()
    {
        var newerSubmittedTime = DateTimeOffset.Parse("2026-08-20T10:00:00Z");
        var olderSubmittedTime = DateTimeOffset.Parse("2026-08-19T10:00:00Z");
        var gate = new object();
        (DateTimeOffset SubmittedTime, string RunId)? latest = null;

        async Task CompleteAsync(DateTimeOffset submittedTime, string runId, int delayMilliseconds)
        {
            await Task.Delay(delayMilliseconds);
            lock (gate)
            {
                if (latest is null || CostStorageRepository.CompareRuns(
                        submittedTime,
                        runId,
                        latest.Value.SubmittedTime,
                        latest.Value.RunId) >= 0)
                {
                    latest = (submittedTime, runId);
                }
            }
        }

        await Task.WhenAll(
            CompleteAsync(olderSubmittedTime, "run-old", 25),
            CompleteAsync(newerSubmittedTime, "run-new", 0));

        latest.Should().NotBeNull();
        latest!.Value.RunId.Should().Be("run-new");
    }

    [Fact]
    public void CompareRuns_WhenSubmittedTimesMatch_ShouldUseRunIdAsStableTieBreaker()
    {
        var submittedTime = DateTimeOffset.Parse("2026-08-20T10:00:00Z");

        CostStorageRepository.CompareRuns(
                submittedTime,
                "run-b",
                submittedTime,
                "run-a")
            .Should().BePositive();
    }

    [Fact]
    public void CompareControlDocuments_WhenLaterUsageWasSubmittedEarlier_ShouldPrioritizeUsageDate()
    {
        CostStorageRepository.CompareControlDocuments(
                new DateTime(2026, 9, 2),
                DateTimeOffset.Parse("2026-09-03T08:00:00Z"),
                "run-september",
                new DateTime(2026, 8, 31),
                DateTimeOffset.Parse("2026-09-03T10:00:00Z"),
                "run-august")
            .Should().BePositive();
    }

    [Fact]
    public void CompareControlDocuments_WhenUsageDatesMatch_ShouldPrioritizeSubmittedTime()
    {
        CostStorageRepository.CompareControlDocuments(
                new DateTime(2026, 9, 2),
                DateTimeOffset.Parse("2026-09-03T10:00:00Z"),
                "run-new",
                new DateTime(2026, 9, 2),
                DateTimeOffset.Parse("2026-09-03T08:00:00Z"),
                "run-old")
            .Should().BePositive();
    }
}
