using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class CostAnomalyDailyCostServiceTests
{
    [Fact]
    public async Task GetDailyCostsAsync_WhenCostReaderFails_ShouldPropagateFailure()
    {
        var reader = new Mock<ICostDataReader>(MockBehavior.Strict);
        reader
            .Setup(c => c.ReadDailyCostsAsync(
                "sub-fail",
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("forbidden"));
        var sut = new CostAnomalyDailyCostService(
            reader.Object,
            new NullLogger<CostAnomalyDailyCostService>());

        var act = () => sut.GetDailyCostsAsync("sub-fail", new DateTime(2026, 8, 12), 7);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("forbidden");
    }

    [Fact]
    public async Task GetDailyCostsAsync_ShouldPreserveExportSourceFromReader()
    {
        var reader = new Mock<ICostDataReader>(MockBehavior.Strict);
        reader.Setup(item => item.ReadDailyCostsAsync(
                "sub-export",
                new DateTime(2026, 8, 16),
                new DateTime(2026, 8, 18),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DailyCostReadResult
            {
                Source = CostDataSources.CostManagementExport,
                DataAsOf = new DateTime(2026, 8, 18),
                Entries =
                [
                    new DailyCostEntry
                    {
                        Date = "2026-08-18",
                        TotalCost = 12,
                        Currency = "BRL",
                        Source = CostDataSources.CostManagementExport
                    }
                ]
            });
        var sut = new CostAnomalyDailyCostService(
            reader.Object,
            new NullLogger<CostAnomalyDailyCostService>());

        var result = await sut.GetDailyCostsAsync(
            "sub-export",
            new DateTime(2026, 8, 18),
            3);

        result.Should().ContainSingle();
        result[0].Source.Should().Be(CostDataSources.CostManagementExport);
    }
}
