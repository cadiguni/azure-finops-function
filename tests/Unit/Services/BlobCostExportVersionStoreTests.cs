using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public sealed class BlobCostExportVersionStoreTests
{
    [Fact]
    public void IsSnapshotReferenceValid_ShouldUsePersistedRevisionForOlderSerializationContract()
    {
        var persistedRevision = new string('a', 64);
        var snapshot = new CostExportDailySnapshot
        {
            SubscriptionId = "SUB-1",
            PublicationMode = "Official",
            Date = new DateTime(2026, 9, 15)
        };

        snapshot.Revision.Should().NotBe(persistedRevision);
        BlobCostExportVersionStore.IsSnapshotReferenceValid(
                snapshot,
                persistedRevision,
                "sub-1",
                "official",
                persistedRevision)
            .Should().BeTrue();
    }

    [Fact]
    public void IsSnapshotReferenceValid_ShouldRejectDifferentPersistedRevision()
    {
        var snapshot = new CostExportDailySnapshot
        {
            SubscriptionId = "sub-1",
            PublicationMode = "Official"
        };

        BlobCostExportVersionStore.IsSnapshotReferenceValid(
                snapshot,
                new string('b', 64),
                "sub-1",
                "Official",
                new string('a', 64))
            .Should().BeFalse();
    }
}
