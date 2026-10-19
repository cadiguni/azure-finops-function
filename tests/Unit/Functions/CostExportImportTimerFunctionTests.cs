using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Functions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Functions;

public sealed class CostExportImportTimerFunctionTests
{
    [Fact]
    public async Task RunAsync_WhenManifestIsPromoted_ShouldEnqueueAnomalyRun()
    {
        var importService = new Mock<ICostExportImportService>(MockBehavior.Strict);
        importService
            .Setup(service => service.ImportLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportImportResult { ManifestsProcessed = 2 });
        var queueService = new Mock<ICostAnomalyQueueService>(MockBehavior.Strict);
        queueService
            .Setup(service => service.EnqueueRunAsync(
                "cost-export-import",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("run-1");
        var sut = CreateFunction(importService.Object, queueService.Object);

        await sut.RunAsync(null!, CancellationToken.None);

        queueService.VerifyAll();
    }

    [Fact]
    public async Task RunAsync_WhenExportPromotesResourceCosts_ShouldEnqueueTechnicalSnapshotAnalysis()
    {
        var importService = new Mock<ICostExportImportService>(MockBehavior.Strict);
        importService.Setup(service => service.ImportLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportImportResult
            {
                ManifestsProcessed = 1,
                AnalysisTargets = [new CostExportAnalysisTarget("sub-1", "run-42")]
            });
        var anomalyQueue = new Mock<ICostAnomalyQueueService>(MockBehavior.Strict);
        anomalyQueue.Setup(service => service.EnqueueRunAsync("cost-export-import", It.IsAny<CancellationToken>()))
            .ReturnsAsync("anomaly-run");
        var technicalQueue = new Mock<IExportTechnicalAnalysisQueue>(MockBehavior.Strict);
        technicalQueue.Setup(service => service.EnqueueAsync(
                new CostExportAnalysisTarget("sub-1", "run-42"), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var sut = CreateFunction(importService.Object, anomalyQueue.Object, technicalQueue: technicalQueue.Object);

        await sut.RunAsync(null!, CancellationToken.None);

        anomalyQueue.VerifyAll();
        technicalQueue.VerifyAll();
    }

    [Fact]
    public async Task RunAsync_WhenNoManifestIsPromoted_ShouldNotEnqueueAnomalyRun()
    {
        var importService = new Mock<ICostExportImportService>(MockBehavior.Strict);
        importService
            .Setup(service => service.ImportLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportImportResult());
        var queueService = new Mock<ICostAnomalyQueueService>(MockBehavior.Strict);
        var sut = CreateFunction(importService.Object, queueService.Object);

        await sut.RunAsync(null!, CancellationToken.None);

        queueService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_WhenCheckpointExistsAndTodaysReportIsMissing_ShouldEnqueueRecovery()
    {
        var importService = new Mock<ICostExportImportService>(MockBehavior.Strict);
        importService
            .Setup(service => service.ImportLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportImportResult { ManifestsSkippedByCheckpoint = 2 });
        var queueService = new Mock<ICostAnomalyQueueService>(MockBehavior.Strict);
        queueService
            .Setup(service => service.EnqueueRunAsync(
                "cost-export-import-recovery",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("run-recovery");
        var reports = new Mock<ICostAnomalyReportRepository>(MockBehavior.Strict);
        reports
            .Setup(repository => repository.LoadLatestReportAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostAnomalyReport { Date = DateTime.UtcNow.Date.AddDays(-1).ToString("yyyy-MM-dd") });
        var sut = CreateFunction(importService.Object, queueService.Object, reports.Object);

        await sut.RunAsync(null!, CancellationToken.None);

        queueService.VerifyAll();
        reports.VerifyAll();
    }

    [Fact]
    public async Task RunAsync_WhenCheckpointExistsAndTodaysReportExists_ShouldNotEnqueueRecovery()
    {
        var importService = new Mock<ICostExportImportService>(MockBehavior.Strict);
        importService
            .Setup(service => service.ImportLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportImportResult { ManifestsSkippedByCheckpoint = 2 });
        var queueService = new Mock<ICostAnomalyQueueService>(MockBehavior.Strict);
        var reports = new Mock<ICostAnomalyReportRepository>(MockBehavior.Strict);
        reports
            .Setup(repository => repository.LoadLatestReportAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostAnomalyReport { Date = DateTime.UtcNow.Date.ToString("yyyy-MM-dd") });
        var sut = CreateFunction(importService.Object, queueService.Object, reports.Object);

        await sut.RunAsync(null!, CancellationToken.None);

        queueService.VerifyNoOtherCalls();
        reports.VerifyAll();
    }

    [Fact]
    public async Task RunAsync_WhenAnomalyAnalysisIsDisabled_ShouldNotEnqueueAnomalyRun()
    {
        var importService = new Mock<ICostExportImportService>(MockBehavior.Strict);
        importService
            .Setup(service => service.ImportLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportImportResult { ManifestsProcessed = 1 });
        var queueService = new Mock<ICostAnomalyQueueService>(MockBehavior.Strict);
        var sut = CreateFunction(
            importService.Object,
            queueService.Object,
            enableAnomalyAnalysis: false);

        await sut.RunAsync(null!, CancellationToken.None);

        queueService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_WhenImportIsPartial_ShouldEnqueuePromotedDataAndPreserveFailure()
    {
        var result = new CostExportImportResult
        {
            ManifestsProcessed = 2,
            Failures = ["subscription failed"]
        };
        var importService = new Mock<ICostExportImportService>(MockBehavior.Strict);
        importService
            .Setup(service => service.ImportLatestAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CostExportImportException(result));
        var queueService = new Mock<ICostAnomalyQueueService>(MockBehavior.Strict);
        queueService
            .Setup(service => service.EnqueueRunAsync(
                "cost-export-import",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("run-partial");
        var sut = CreateFunction(importService.Object, queueService.Object);

        var act = () => sut.RunAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<CostExportImportException>();
        queueService.VerifyAll();
    }

    private static CostExportImportTimerFunction CreateFunction(
        ICostExportImportService importService,
        ICostAnomalyQueueService queueService,
        ICostAnomalyReportRepository? reports = null,
        bool enableAnomalyAnalysis = true,
        IExportTechnicalAnalysisQueue? technicalQueue = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableCostExportImport"] = "true",
                ["EnableCostAnomalyAnalysis"] = enableAnomalyAnalysis.ToString()
            })
            .Build();

        return new CostExportImportTimerFunction(
            importService,
            queueService,
            technicalQueue ?? Mock.Of<IExportTechnicalAnalysisQueue>(),
            reports ?? Mock.Of<ICostAnomalyReportRepository>(),
            configuration,
            NullLogger<CostExportImportTimerFunction>.Instance);
    }
}
