using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class ResourceCostLookupServiceTests
{
    private const string Id = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Sql/servers/s/databases/db";

    [Fact]
    public async Task LookupsAndBatch_ShouldShareOneReadWithoutCallingAzureDirectly()
    {
        var reader = Reader();
        var azure = new Mock<ICostManagementClient>(MockBehavior.Strict);
        var service = Create(reader, azure);
        await service.PreloadCostsAsync("sub");
        var single = await service.GetResourceCostDataAsync("sub", Id);
        var batch = await service.GetResourceCostsAsync("sub", new[] { Id });
        single.ObservedCost.Should().Be(30);
        single.DailyCost.Should().Be(15); // One charged day and one observed zero day.
        single.MonthlyCost.Should().Be(450);
        single.Provenance!.Source.Should().Be(CostDataSources.CostManagementExport);
        single.Provenance.ExportRunId.Should().Be("run-1");
        batch[Id].Should().BeEquivalentTo(single);
        reader.Verify(r => r.ReadResourceCostsAsync("sub", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        azure.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExplicitPeriodAndDiagnostics_ShouldUseSharedReaderAndKeepRequestedPeriod()
    {
        var reader = Reader();
        var service = Create(reader);
        var from = new DateTime(2026, 8, 1);
        var to = new DateTime(2026, 8, 31);
        var result = await service.GetResourceCostDataAsync("sub", Id, null, null, from, to);
        result.CostPeriodFrom.Should().Be(from);
        result.CostPeriodTo.Should().Be(to);
        var diagnostic = await service.GetCostMatchDiagnosticsAsync(Id, "2026-08");
        diagnostic.Provenance!.ExportRunId.Should().Be("run-1");
        reader.Verify(r => r.ReadResourceCostsAsync("sub", from, to, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task MissingResource_ShouldNotQueryAgainAndObservedCostShouldRemainNull()
    {
        var reader = Reader();
        var service = Create(reader);
        var result = await service.GetResourceCostDataAsync("sub", "/subscriptions/sub/resourceGroups/other/providers/Microsoft.Compute/disks/missing");
        result.ObservedCost.Should().BeNull();
        result.CostSource.Should().Be(CostMatchSources.NotMatched);
        result.Provenance!.Source.Should().Be(CostDataSources.CostManagementExport);
        reader.Verify(r => r.ReadResourceCostsAsync("sub", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailedSources_ShouldBeUnavailableAndNotRepeatedForEachResource()
    {
        var reader = Reader();
        reader.Setup(r => r.ReadResourceCostsAsync("sub", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("both sources failed"));
        var service = Create(reader);
        var result = await service.GetResourceCostDataAsync("sub", Id);
        await service.GetResourceCostDataAsync("sub", Id);
        result.ObservedCost.Should().BeNull();
        result.Provenance!.Source.Should().Be("Unavailable");
        result.CostNote.Should().Contain("temporariamente indisponível");
        result.CostNote.Should().NotContain("não encontrado");
        reader.Verify(r => r.ReadResourceCostsAsync("sub", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancellation_ShouldNotBecomeMissingCostsOrPopulateFailureCache()
    {
        var reader = Reader();
        reader.SetupSequence(r => r.ReadResourceCostsAsync("sub", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException())
            .ReturnsAsync(ReadResult());
        var service = Create(reader);
        await service.Invoking(s => s.GetResourceCostDataAsync("sub", Id)).Should().ThrowAsync<OperationCanceledException>();
        (await service.GetResourceCostDataAsync("sub", Id)).ObservedCost.Should().Be(30);
    }

    [Fact]
    public async Task ClearCache_ShouldReadNewPublicationOnNextLookup()
    {
        var reader = Reader();
        var service = Create(reader);
        await service.GetResourceCostDataAsync("sub", Id);
        service.ClearCache();
        await service.GetResourceCostDataAsync("sub", Id);
        reader.Verify(r => r.ReadResourceCostsAsync("sub", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static ResourceCostLookupService Create(Mock<ICostDataReader> reader, Mock<ICostManagementClient>? azure = null)
        => new((azure ?? new Mock<ICostManagementClient>(MockBehavior.Strict)).Object, reader.Object,
            new ResourceCostResolver(), NullLogger<ResourceCostLookupService>.Instance);

    private static Mock<ICostDataReader> Reader()
    {
        var reader = new Mock<ICostDataReader>(MockBehavior.Strict);
        reader.Setup(r => r.ReadResourceCostsAsync("sub", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReadResult());
        return reader;
    }

    private static ResourceCostReadResult ReadResult() => new()
    {
        Provenance = new CostReadProvenance { Source = CostDataSources.CostManagementExport, ExportRunId = "run-1" },
        Rows = new[]
        {
            new CostDiagnosticRow { ResourceId = Id, UsageDate = DateTime.UtcNow.Date.AddDays(-2), TotalCost = 30 },
            new CostDiagnosticRow { ResourceId = Id, UsageDate = DateTime.UtcNow.Date.AddDays(-1), TotalCost = 0 }
        }
    };
}
