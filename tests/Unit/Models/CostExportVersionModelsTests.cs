using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Models;

public class CostExportVersionModelsTests
{
    private const string SubscriptionId = "sub-001";
    private const string Mode = CostExportPublicationModes.Shadow;
    private static readonly DateTime UsageDate = new(2026, 7, 20);

    [Fact]
    public void Merge_ShouldPromoteNewerRetroactiveCorrection()
    {
        var original = Snapshot("run-1", new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero), 100m);
        var corrected = Snapshot("run-2", new DateTimeOffset(2026, 7, 21, 9, 0, 0, TimeSpan.Zero), 125m);
        var originalIndex = EmptyIndex().Merge([original]);

        var correctedIndex = originalIndex.Merge([corrected]);

        correctedIndex.Days["2026-07-20"].Revision.Should().Be(corrected.Revision);
        correctedIndex.Revision.Should().NotBe(originalIndex.Revision);
    }

    [Fact]
    public void Merge_ShouldNotReplaceCorrectionWithOlderDelivery()
    {
        var corrected = Snapshot("run-2", new DateTimeOffset(2026, 7, 21, 9, 0, 0, TimeSpan.Zero), 125m);
        var delayed = Snapshot("run-1", new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero), 100m);
        var correctedIndex = EmptyIndex().Merge([corrected]);

        var result = correctedIndex.Merge([delayed]);

        result.Days["2026-07-20"].Revision.Should().Be(corrected.Revision);
        result.Revision.Should().Be(correctedIndex.Revision);
    }

    [Fact]
    public void Merge_ShouldRejectDifferentContentForSameRunAndDate()
    {
        var submittedAt = new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero);
        var original = Snapshot("run-1", submittedAt, 100m);
        var inconsistentRetry = Snapshot("run-1", submittedAt, 125m);
        var index = EmptyIndex().Merge([original]);

        var action = () => index.Merge([inconsistentRetry]);

        action.Should().Throw<InvalidDataException>()
            .WithMessage("*mesmo run retornou conteúdos diferentes*");
    }

    [Fact]
    public void Merge_ShouldPromoteTombstoneForDateRemovedByNewerRun()
    {
        var original = Snapshot("run-1", new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero), 100m);
        var tombstone = new CostExportDailySnapshot
        {
            SubscriptionId = SubscriptionId,
            PublicationMode = Mode,
            Date = UsageDate,
            ExportName = "daily-export",
            RunId = "run-2",
            SubmittedTime = new DateTimeOffset(2026, 7, 21, 9, 0, 0, TimeSpan.Zero),
            ImportedAt = new DateTimeOffset(2026, 7, 21, 9, 5, 0, TimeSpan.Zero),
            IsTombstone = true,
            TombstoneCurrency = "BRL"
        };

        var result = EmptyIndex().Merge([original]).Merge([tombstone]);

        result.Days["2026-07-20"].Revision.Should().Be(tombstone.Revision);
        tombstone.Revision.Should().NotBe(original.Revision);
    }

    private static CostExportRevisionIndex EmptyIndex() => new()
    {
        SubscriptionId = SubscriptionId,
        PublicationMode = Mode
    };

    private static CostExportDailySnapshot Snapshot(string runId, DateTimeOffset submittedAt, decimal cost) => new()
    {
        SubscriptionId = SubscriptionId,
        PublicationMode = Mode,
        Date = UsageDate,
        ExportName = "daily-export",
        RunId = runId,
        SubmittedTime = submittedAt,
        ImportedAt = submittedAt.AddMinutes(5),
        ByService = [new CostByServiceRow { Label = "Compute", Currency = "BRL", TotalCost = cost }]
    };
}
