using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public sealed class BlobCostExportBlobSourceTests
{
    [Fact]
    public void OrderManifestNamesNewestFirst_ShouldPrioritizeLatestRuns()
    {
        var manifests = new[]
        {
            new BlobCostExportBlobSource.ManifestBlobCandidate(
                "subscriptions/sub/billing-period/current/export/20260801-20260831/old/manifest.json",
                DateTimeOffset.Parse("2026-09-04T10:00:00Z")),
            new BlobCostExportBlobSource.ManifestBlobCandidate(
                "subscriptions/sub/billing-period/current/export/20260901-20260930/current/manifest.json",
                DateTimeOffset.Parse("2026-09-03T10:00:00Z")),
            new BlobCostExportBlobSource.ManifestBlobCandidate(
                "subscriptions/sub/billing-period/current/export/20260901-20260930/previous/manifest.json",
                DateTimeOffset.Parse("2026-09-02T10:00:00Z"))
        };

        var result = BlobCostExportBlobSource.OrderManifestNamesNewestFirst(manifests);

        result.Should().ContainInOrder(
            manifests[1].Name,
            manifests[2].Name,
            manifests[0].Name);
    }

    [Fact]
    public void OrderManifestNamesNewestFirst_WhenPeriodIsUnavailable_ShouldUseLastModified()
    {
        var manifests = new[]
        {
            new BlobCostExportBlobSource.ManifestBlobCandidate(
                "subscriptions/sub/export/old/manifest.json",
                DateTimeOffset.Parse("2026-09-02T10:00:00Z")),
            new BlobCostExportBlobSource.ManifestBlobCandidate(
                "subscriptions/sub/export/current/manifest.json",
                DateTimeOffset.Parse("2026-09-03T10:00:00Z"))
        };

        var result = BlobCostExportBlobSource.OrderManifestNamesNewestFirst(manifests);

        result.Should().ContainInOrder(manifests[1].Name, manifests[0].Name);
    }
}
