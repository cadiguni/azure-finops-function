using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public sealed class CostExportImportServiceTests
{
    private const string SubscriptionOne = "00000000-0000-0000-0000-00000000a006";
    private const string SubscriptionTwo = "00000000-0000-0000-0000-00000000a007";
    private const string SubscriptionThree = "00000000-0000-0000-0000-00000000a005";
    private const string TargetExport = "finops-actual-cost-current-billing";

    [Fact]
    public async Task ImportLatestAsync_ShouldProcessEveryPartitionAndPublishSnapshot()
    {
        var firstPartition = PartitionName(SubscriptionOne, "run-1", 1);
        var secondPartition = PartitionName(SubscriptionOne, "run-1", 2);
        var manifestName = ManifestName(SubscriptionOne, "run-1");
        var source = new FakeExportSource(
            [manifestName],
            new Dictionary<string, string>
            {
                [manifestName] = ManifestJson(
                    SubscriptionOne,
                    "run-1",
                    "2026-08-19T19:30:53Z",
                    [(firstPartition, 1L), (secondPartition, 1L)])
            },
            new Dictionary<string, byte[]>
            {
                [firstPartition] = Utf8(Csv(SubscriptionOne, "2026-08-17", "Storage", "stg", "1.25")),
                [secondPartition] = Utf8(Csv(SubscriptionOne, "2026-08-18", "Storage", "stg", "2.75"))
            });
        var repository = CreateRepository();
        var publishedServiceDates = new List<DateTime>();
        var publishedResourceDates = new List<DateTime>();
        var publicationModes = new List<string>();
        CostExportImportCheckpoint? checkpoint = null;
        CostExportPublicationWatermark? watermark = null;
        repository
            .Setup(item => item.SaveExportByServiceAsync(
                It.IsAny<DateTime>(),
                SubscriptionOne,
                It.IsAny<IReadOnlyCollection<CostByServiceRow>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<DateTime, string, IReadOnlyCollection<CostByServiceRow>, string, CancellationToken>(
                (date, _, _, mode, _) =>
                {
                    publishedServiceDates.Add(date);
                    publicationModes.Add(mode);
                })
            .Returns(Task.CompletedTask);
        repository
            .Setup(item => item.SaveExportByResourceAsync(
                It.IsAny<DateTime>(),
                SubscriptionOne,
                It.IsAny<IReadOnlyCollection<CostByResourceRow>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<DateTime, string, IReadOnlyCollection<CostByResourceRow>, string, CancellationToken>(
                (date, _, _, _, _) => publishedResourceDates.Add(date))
            .Returns(Task.CompletedTask);
        repository
            .Setup(item => item.SavePublicationWatermarkAsync(
                It.IsAny<CostExportPublicationWatermark>(),
                It.IsAny<CancellationToken>()))
            .Callback<CostExportPublicationWatermark, CancellationToken>(
                (value, _) => watermark = value)
            .Returns(Task.CompletedTask);
        repository
            .Setup(item => item.SaveImportRunCheckpointAsync(
                It.IsAny<CostExportImportCheckpoint>(),
                It.IsAny<CancellationToken>()))
            .Callback<CostExportImportCheckpoint, CancellationToken>(
                (value, _) => checkpoint = value)
            .Returns(Task.CompletedTask);

        var result = await CreateService(source, repository.Object).ImportLatestAsync();

        result.ManifestsFound.Should().Be(1);
        result.ManifestsProcessed.Should().Be(1);
        result.PartitionsProcessed.Should().Be(2);
        result.ImportedRows.Should().Be(2);
        result.PublishedDates.Should().Be(2);
        publishedServiceDates.Should().Equal(
            new DateTime(2026, 8, 17),
            new DateTime(2026, 8, 18));
        publishedResourceDates.Should().Equal(publishedServiceDates);
        checkpoint.Should().NotBeNull();
        checkpoint!.Status.Should().Be("Completed");
        checkpoint.PublicationMode.Should().Be(CostExportPublicationModes.Shadow);
        checkpoint.Source.Should().Be(CostDataSources.CostManagementExport);
        checkpoint.RunId.Should().Be("run-1");
        checkpoint.ExportName.Should().Be(TargetExport);
        checkpoint.SubmittedTime.Should().Be(DateTimeOffset.Parse("2026-08-19T19:30:53Z"));
        checkpoint.MinUsageDate.Should().Be(new DateTime(2026, 8, 17));
        checkpoint.MaxUsageDate.Should().Be(new DateTime(2026, 8, 18));
        checkpoint.PartitionsProcessed.Should().Be(2);
        checkpoint.RowsProcessed.Should().Be(2);
        checkpoint.TotalsByCurrency.Should().Contain("USD", 4m);
        watermark.Should().NotBeNull();
        watermark!.Status.Should().Be("Completed");
        watermark.PublicationMode.Should().Be(CostExportPublicationModes.Shadow);
        watermark.Source.Should().Be(CostDataSources.CostManagementExport);
        watermark.MinUsageDate.Should().Be(new DateTime(2026, 8, 17));
        watermark.MaxUsageDate.Should().Be(new DateTime(2026, 8, 18));
        watermark.RowsProcessed.Should().Be(2);
        watermark.PartitionsProcessed.Should().Be(2);
        watermark.PublishedBlobs.Should().HaveCount(6);
        watermark.PublishedBlobs.Should().OnlyContain(path => path.StartsWith("cost/shadow/"));
        publicationModes.Should().OnlyContain(mode => mode == CostExportPublicationModes.Shadow);
        watermark.TotalsByCurrency.Should().Contain("USD", 4m);
        repository.Invocations
            .Select(invocation => invocation.Method.Name)
            .Where(name => name is
                nameof(ICostStorageRepository.SaveImportRunCheckpointAsync) or
                nameof(ICostStorageRepository.UpdateLatestImportCheckpointAsync) or
                nameof(ICostStorageRepository.SavePublicationWatermarkAsync))
            .Should().Equal(
                nameof(ICostStorageRepository.SaveImportRunCheckpointAsync),
                nameof(ICostStorageRepository.UpdateLatestImportCheckpointAsync),
                nameof(ICostStorageRepository.SavePublicationWatermarkAsync));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public async Task ImportLatestAsync_WhenManifestTotalsMismatch_ShouldNotPublishCheckpoint(
        int blobCount,
        long dataRowCount)
    {
        var partition = PartitionName(SubscriptionOne, "run-invalid", 1);
        var manifestName = ManifestName(SubscriptionOne, "run-invalid");
        var source = new FakeExportSource(
            [manifestName],
            new Dictionary<string, string>
            {
                [manifestName] = ManifestJson(
                    SubscriptionOne,
                    "run-invalid",
                    "2026-08-19T19:30:53Z",
                    [(partition, 1L)],
                    blobCount,
                    dataRowCount)
            },
            new Dictionary<string, byte[]>
            {
                [partition] = Utf8(Csv(SubscriptionOne, "2026-08-18", "Storage", "stg", "1.25"))
            });
        var repository = CreateRepository();

        var act = () => CreateService(source, repository.Object).ImportLatestAsync();

        var exception = await act.Should().ThrowAsync<CostExportImportException>();
        exception.Which.Result.Failures.Should().ContainSingle();
        repository.Verify(item => item.SaveExportByServiceAsync(
            It.IsAny<DateTime>(),
            It.IsAny<string>(),
            It.IsAny<IReadOnlyCollection<CostByServiceRow>>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(item => item.SaveImportRunCheckpointAsync(
            It.IsAny<CostExportImportCheckpoint>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("subscriptions/{subscription}/billing-period/current/finops-actual-cost-current-billing/run/../part.csv")]
    [InlineData("subscriptions/00000000-0000-0000-0000-00000000a007/billing-period/current/finops-actual-cost-current-billing/run/part.csv")]
    [InlineData("subscriptions/{subscription}/billing-period/current/another-export/run/part.csv")]
    [InlineData("https://storage.example/container/part.csv")]
    public async Task ImportLatestAsync_WhenPartitionPathEscapesManifestScope_ShouldReject(
        string partitionPattern)
    {
        var manifestName = ManifestName(SubscriptionOne, "run-path");
        var invalidPartition = partitionPattern.Replace("{subscription}", SubscriptionOne);
        var source = new FakeExportSource(
            [manifestName],
            new Dictionary<string, string>
            {
                [manifestName] = ManifestJson(
                    SubscriptionOne,
                    "run-path",
                    "2026-08-19T19:30:53Z",
                    [(invalidPartition, 1L)])
            },
            new Dictionary<string, byte[]>());
        var repository = CreateRepository();

        var act = () => CreateService(source, repository.Object).ImportLatestAsync();

        var exception = await act.Should().ThrowAsync<CostExportImportException>();
        exception.Which.Result.Failures.Should().ContainSingle();
        source.StreamReads.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Usage", "BillingMonthToDate")]
    [InlineData("ActualCost", "MonthToDate")]
    [InlineData("ActualCost", "BillingMonthToDate")]
    [InlineData("AmortizedCost", "MonthToDate")]
    public async Task ImportLatestAsync_WhenMainExportContractDiffersFromUsageMonthToDate_ShouldReject(
        string exportType,
        string timeframe)
    {
        var manifestName = ManifestName(SubscriptionOne, "run-contract");
        var partition = PartitionName(SubscriptionOne, "run-contract", 1);
        var source = new FakeExportSource(
            [manifestName],
            new Dictionary<string, string>
            {
                [manifestName] = ManifestJson(
                    SubscriptionOne,
                    "run-contract",
                    "2026-08-20T10:00:00Z",
                    [(partition, 1L)],
                    exportType: exportType,
                    timeframe: timeframe)
            },
            new Dictionary<string, byte[]>());

        var act = () => CreateService(source, CreateRepository().Object).ImportLatestAsync();

        var exception = await act.Should().ThrowAsync<CostExportImportException>();
        exception.Which.Result.Failures.Should().ContainSingle(message =>
            message.Contains("Usage + MonthToDate", StringComparison.Ordinal));
        source.StreamReads.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportLatestAsync_ShouldUseCurrentPathAndPilotAllowlistOnly()
    {
        var allowedManifest = ManifestName(SubscriptionOne, "run-allowed");
        var deniedManifest = ManifestName(SubscriptionTwo, "run-denied");
        var lastManifest = allowedManifest.Replace(
            "/billing-period/current/",
            "/billing-period/last/");
        var partition = PartitionName(SubscriptionOne, "run-allowed", 1);
        var source = new FakeExportSource(
            [lastManifest, deniedManifest, allowedManifest],
            new Dictionary<string, string>
            {
                [allowedManifest] = ManifestJson(
                    SubscriptionOne,
                    "run-allowed",
                    "2026-08-19T19:30:53Z",
                    [(partition, 1L)])
            },
            new Dictionary<string, byte[]>
            {
                [partition] = Utf8(Csv(SubscriptionOne, "2026-08-18", "Storage", "stg", "1"))
            });
        var repository = CreateRepository();
        var configuration = Settings(SubscriptionOne);

        var result = await CreateService(source, repository.Object, configuration)
            .ImportLatestAsync();

        result.ManifestsFound.Should().Be(1);
        result.SubscriptionsProcessed.Should().Be(1);
        source.TextReads.Should().Equal(allowedManifest);
    }

    [Fact]
    public async Task ImportLatestAsync_WhenCompletedCheckpointExists_ShouldSkipRun()
    {
        var manifestName = ManifestName(SubscriptionOne, "run-completed");
        var partition = PartitionName(SubscriptionOne, "run-completed", 1);
        var source = new FakeExportSource(
            [manifestName],
            new Dictionary<string, string>
            {
                [manifestName] = ManifestJson(
                    SubscriptionOne,
                    "run-completed",
                    "2026-08-19T19:30:53Z",
                    [(partition, 1L)])
            },
            new Dictionary<string, byte[]>());
        var repository = CreateRepository(checkpointCompleted: true);

        var result = await CreateService(source, repository.Object).ImportLatestAsync();

        result.ManifestsSkippedByCheckpoint.Should().Be(1);
        result.ManifestsProcessed.Should().Be(0);
        source.StreamReads.Should().BeEmpty();
        repository.Verify(item => item.SavePublicationWatermarkAsync(
            It.IsAny<CostExportPublicationWatermark>(),
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.UpdateLatestImportCheckpointAsync(
            It.IsAny<CostExportImportCheckpoint>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ImportLatestAsync_WhenNewUsageManifestIsListedFirst_ShouldRejectOlderLegacyContract()
    {
        var oldManifest = ManifestName(SubscriptionOne, "run-old");
        var newManifest = ManifestName(SubscriptionOne, "run-new");
        var oldPartition = PartitionName(SubscriptionOne, "run-old", 1);
        var newPartition = PartitionName(SubscriptionOne, "run-new", 1);
        var source = new FakeExportSource(
            [newManifest, oldManifest],
            new Dictionary<string, string>
            {
                [newManifest] = ManifestJson(
                    SubscriptionOne,
                    "run-new",
                    "2026-08-20T10:00:00Z",
                    [(newPartition, 1L)]),
                [oldManifest] = ManifestJson(
                    SubscriptionOne,
                    "run-old",
                    "2026-08-19T10:00:00Z",
                    [(oldPartition, 1L)],
                    exportType: "ActualCost",
                    timeframe: "BillingMonthToDate")
            },
            new Dictionary<string, byte[]>
            {
                [oldPartition] = Utf8(Csv(SubscriptionOne, "2026-08-18", "Storage", "stg", "1")),
                [newPartition] = Utf8(Csv(SubscriptionOne, "2026-08-18", "Storage", "stg", "2"))
            });
        var repository = CreateRepository();
        var checkpointOrder = new List<string>();
        repository
            .Setup(item => item.SaveImportRunCheckpointAsync(
                It.IsAny<CostExportImportCheckpoint>(),
                It.IsAny<CancellationToken>()))
            .Callback<CostExportImportCheckpoint, CancellationToken>(
                (checkpoint, _) => checkpointOrder.Add(checkpoint.RunId))
            .Returns(Task.CompletedTask);

        var act = () => CreateService(source, repository.Object).ImportLatestAsync();

        var exception = await act.Should().ThrowAsync<CostExportImportException>();
        exception.Which.Result.ManifestsProcessed.Should().Be(1);
        exception.Which.Result.Failures.Should().ContainSingle(message =>
            message.Contains("Usage + MonthToDate", StringComparison.Ordinal));
        checkpointOrder.Should().Equal("run-new");
    }

    [Fact]
    public async Task ImportLatestAsync_WhenOlderRunIsIngested_ShouldNotOverwriteNewerSelectedDay()
    {
        var oldManifest = ManifestName(SubscriptionOne, "run-old");
        var oldPartition = PartitionName(SubscriptionOne, "run-old", 1);
        var source = new FakeExportSource(
            [oldManifest],
            new Dictionary<string, string>
            {
                [oldManifest] = ManifestJson(
                    SubscriptionOne,
                    "run-old",
                    "2026-08-19T10:00:00Z",
                    [(oldPartition, 1L)])
            },
            new Dictionary<string, byte[]>
            {
                [oldPartition] = Utf8(Csv(SubscriptionOne, "2026-08-18", "Storage", "stg", "1"))
            });
        var repository = CreateRepository();
        repository
            .Setup(item => item.LoadLatestCompletedImportCheckpointAsync(
                SubscriptionOne,
                TargetExport,
                CostExportPublicationModes.Shadow,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportImportCheckpoint
            {
                SubscriptionId = SubscriptionOne,
                ExportName = TargetExport,
                RunId = "run-new",
                SubmittedTime = DateTimeOffset.Parse("2026-08-20T10:00:00Z"),
                Status = "Completed"
            });
        var versions = new Mock<ICostExportVersionStore>();
        versions.Setup(item => item.PublishAsync(
                It.IsAny<CostExportImportCheckpoint>(),
                It.IsAny<IReadOnlyList<CostExportDailySnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        versions.Setup(item => item.LoadIndexAsync(
                SubscriptionOne,
                CostExportPublicationModes.Shadow,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportRevisionIndex
            {
                SubscriptionId = SubscriptionOne,
                PublicationMode = CostExportPublicationModes.Shadow,
                Days = new SortedDictionary<string, CostExportDayReference>(StringComparer.Ordinal)
                {
                    ["2026-08-18"] = new()
                    {
                        Revision = new string('a', 64),
                        RunId = "run-new",
                        SubmittedTime = DateTimeOffset.Parse("2026-08-20T10:00:00Z")
                    }
                }
            });

        var result = await CreateService(source, repository.Object, versions: versions.Object).ImportLatestAsync();

        result.ManifestsProcessed.Should().Be(1);
        repository.Verify(item => item.SaveExportByServiceAsync(
            It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<CostByServiceRow>>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ImportLatestAsync_WhenAuthoritativeRunRemovesDay_ShouldPublishTombstoneAndClearLegacyMirrors()
    {
        var partition = PartitionName(SubscriptionOne, "run-correction", 1);
        var manifestName = ManifestName(SubscriptionOne, "run-correction");
        var source = new FakeExportSource(
            [manifestName],
            new Dictionary<string, string>
            {
                [manifestName] = ManifestJson(
                    SubscriptionOne,
                    "run-correction",
                    "2026-08-20T10:00:00Z",
                    [(partition, 1L)],
                    startDate: "2026-08-17",
                    endDate: "2026-08-18")
            },
            new Dictionary<string, byte[]>
            {
                [partition] = Utf8(Csv(SubscriptionOne, "2026-08-18", "Storage", "stg", "2"))
            });
        var previousSnapshot = new CostExportDailySnapshot
        {
            SubscriptionId = SubscriptionOne,
            PublicationMode = CostExportPublicationModes.Shadow,
            Date = new DateTime(2026, 8, 17),
            ExportName = TargetExport,
            RunId = "run-original",
            SubmittedTime = DateTimeOffset.Parse("2026-08-19T10:00:00Z"),
            ImportedAt = DateTimeOffset.Parse("2026-08-19T10:05:00Z"),
            ByService = [new CostByServiceRow { SubscriptionId = SubscriptionOne, Currency = "USD", TotalCost = 5m }]
        };
        var currentIndex = new CostExportRevisionIndex
        {
            SubscriptionId = SubscriptionOne,
            PublicationMode = CostExportPublicationModes.Shadow
        }.Merge([previousSnapshot]);
        IReadOnlyList<CostExportDailySnapshot>? published = null;
        var versions = new Mock<ICostExportVersionStore>();
        versions.Setup(item => item.LoadIndexAsync(
                SubscriptionOne, CostExportPublicationModes.Shadow, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => currentIndex);
        versions.Setup(item => item.LoadSnapshotAsync(
                SubscriptionOne, CostExportPublicationModes.Shadow, previousSnapshot.Revision, It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousSnapshot);
        versions.Setup(item => item.PublishAsync(
                It.IsAny<CostExportImportCheckpoint>(),
                It.IsAny<IReadOnlyList<CostExportDailySnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Callback<CostExportImportCheckpoint, IReadOnlyList<CostExportDailySnapshot>, CancellationToken>((_, snapshots, _) =>
            {
                published = snapshots;
                currentIndex = currentIndex.Merge(snapshots);
            })
            .Returns(Task.CompletedTask);
        var repository = CreateRepository();

        var result = await CreateService(source, repository.Object, versions: versions.Object).ImportLatestAsync();

        result.PublishedDates.Should().Be(2);
        result.AnalysisTargets.Should().ContainSingle(target =>
            target.SubscriptionId == SubscriptionOne && target.RunId == "run-correction");
        published.Should().ContainSingle(snapshot => snapshot.Date == new DateTime(2026, 8, 17)
            && snapshot.IsTombstone
            && snapshot.TombstoneCurrency == "USD");
        repository.Verify(item => item.SaveExportByServiceAsync(
            new DateTime(2026, 8, 17),
            SubscriptionOne,
            It.Is<IReadOnlyCollection<CostByServiceRow>>(rows => rows.Count == 0),
            CostExportPublicationModes.Shadow,
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.SaveExportByResourceAsync(
            new DateTime(2026, 8, 17),
            SubscriptionOne,
            It.Is<IReadOnlyCollection<CostByResourceRow>>(rows => rows.Count == 0),
            CostExportPublicationModes.Shadow,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ImportLatestAsync_WhenNewBillingPeriodWasSubmittedBeforeLatePreviousPeriod_ShouldPromoteNewerUsage()
    {
        var septemberManifest = ManifestName(
            SubscriptionOne,
            "run-september",
            "20260901-20260930");
        var septemberPartition = PartitionName(
            SubscriptionOne,
            "run-september",
            1,
            "20260901-20260930");
        var source = new FakeExportSource(
            [septemberManifest],
            new Dictionary<string, string>
            {
                [septemberManifest] = ManifestJson(
                    SubscriptionOne,
                    "run-september",
                    "2026-09-03T11:17:01Z",
                    [(septemberPartition, 1L)])
            },
            new Dictionary<string, byte[]>
            {
                [septemberPartition] = Utf8(
                    Csv(SubscriptionOne, "2026-09-02", "Storage", "stg", "10"))
            });
        var repository = CreateRepository();
        var augustCheckpoint = new CostExportImportCheckpoint
        {
            SubscriptionId = SubscriptionOne,
            ExportName = TargetExport,
            RunId = "run-august-late",
            PublicationMode = CostExportPublicationModes.Shadow,
            SubmittedTime = DateTimeOffset.Parse("2026-09-03T11:17:02Z"),
            MaxUsageDate = new DateTime(2026, 8, 31),
            Status = "Completed"
        };
        repository
            .Setup(item => item.LoadLatestCompletedImportCheckpointAsync(
                SubscriptionOne,
                TargetExport,
                CostExportPublicationModes.Shadow,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(augustCheckpoint);
        repository
            .Setup(item => item.LoadPublicationWatermarkAsync(
                SubscriptionOne,
                CostExportPublicationModes.Shadow,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportPublicationWatermark
            {
                SubscriptionId = SubscriptionOne,
                ExportName = TargetExport,
                RunId = augustCheckpoint.RunId,
                PublicationMode = CostExportPublicationModes.Shadow,
                SubmittedTime = augustCheckpoint.SubmittedTime,
                MaxUsageDate = augustCheckpoint.MaxUsageDate,
                Status = "Completed"
            });

        var result = await CreateService(source, repository.Object).ImportLatestAsync();

        result.ManifestsProcessed.Should().Be(1);
        result.ManifestsSkippedByCheckpoint.Should().Be(0);
        source.StreamReads.Should().ContainSingle().Which.Should().Be(septemberPartition);
        repository.Verify(item => item.UpdateLatestImportCheckpointAsync(
            It.Is<CostExportImportCheckpoint>(checkpoint =>
                checkpoint.RunId == "run-september" &&
                checkpoint.MaxUsageDate == new DateTime(2026, 9, 2)),
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.SavePublicationWatermarkAsync(
            It.Is<CostExportPublicationWatermark>(watermark =>
                watermark.RunId == "run-september" &&
                watermark.MaxUsageDate == new DateTime(2026, 9, 2)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ImportLatestAsync_WhenPreviousPeriodHadNoUsage_ShouldStillProcessNewBillingPeriod()
    {
        var septemberManifest = ManifestName(
            SubscriptionOne,
            "run-september",
            "20260901-20260930");
        var septemberPartition = PartitionName(
            SubscriptionOne,
            "run-september",
            1,
            "20260901-20260930");
        var source = new FakeExportSource(
            [septemberManifest],
            new Dictionary<string, string>
            {
                [septemberManifest] = ManifestJson(
                    SubscriptionOne,
                    "run-september",
                    "2026-09-03T01:24:01Z",
                    [(septemberPartition, 1L)])
            },
            new Dictionary<string, byte[]>
            {
                [septemberPartition] = Utf8(
                    Csv(SubscriptionOne, "2026-09-02", "Storage", "stg", "10"))
            });
        var repository = CreateRepository();
        repository
            .Setup(item => item.LoadLatestCompletedImportCheckpointAsync(
                SubscriptionOne,
                TargetExport,
                CostExportPublicationModes.Shadow,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportImportCheckpoint
            {
                SubscriptionId = SubscriptionOne,
                ExportName = TargetExport,
                RunId = "run-august-empty",
                PublicationMode = CostExportPublicationModes.Shadow,
                SourceManifest = ManifestName(
                    SubscriptionOne,
                    "run-august-empty",
                    "20260801-20260831"),
                SubmittedTime = DateTimeOffset.Parse("2026-09-03T01:24:03Z"),
                RowsProcessed = 0,
                MaxUsageDate = null,
                Status = "Completed"
            });

        var result = await CreateService(source, repository.Object).ImportLatestAsync();

        result.ManifestsProcessed.Should().Be(1);
        source.StreamReads.Should().ContainSingle().Which.Should().Be(septemberPartition);
        repository.Verify(item => item.SavePublicationWatermarkAsync(
            It.Is<CostExportPublicationWatermark>(watermark =>
                watermark.RunId == "run-september" &&
                watermark.MaxUsageDate == new DateTime(2026, 9, 2)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ImportLatestAsync_WhenLateRunContainsOlderUsage_ShouldPublishHistoryWithoutRegressingWatermark()
    {
        var historicalManifest = ManifestName(SubscriptionOne, "run-historical");
        var historicalPartition = PartitionName(SubscriptionOne, "run-historical", 1);
        var source = new FakeExportSource(
            [historicalManifest],
            new Dictionary<string, string>
            {
                [historicalManifest] = ManifestJson(
                    SubscriptionOne,
                    "run-historical",
                    "2026-09-03T10:00:00Z",
                    [(historicalPartition, 1L)])
            },
            new Dictionary<string, byte[]>
            {
                [historicalPartition] = Utf8(
                    Csv(SubscriptionOne, "2026-08-31", "Storage", "stg", "10"))
            });
        var repository = CreateRepository();
        repository
            .Setup(item => item.LoadLatestCompletedImportCheckpointAsync(
                SubscriptionOne,
                TargetExport,
                CostExportPublicationModes.Shadow,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportImportCheckpoint
            {
                SubscriptionId = SubscriptionOne,
                ExportName = TargetExport,
                RunId = "run-current",
                PublicationMode = CostExportPublicationModes.Shadow,
                SubmittedTime = DateTimeOffset.Parse("2026-09-02T10:00:00Z"),
                MaxUsageDate = new DateTime(2026, 9, 2),
                Status = "Completed"
            });
        repository
            .Setup(item => item.LoadPublicationWatermarkAsync(
                SubscriptionOne,
                CostExportPublicationModes.Shadow,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportPublicationWatermark
            {
                SubscriptionId = SubscriptionOne,
                RunId = "run-current",
                PublicationMode = CostExportPublicationModes.Shadow,
                SubmittedTime = DateTimeOffset.Parse("2026-09-02T10:00:00Z"),
                MaxUsageDate = new DateTime(2026, 9, 2),
                Status = "Completed"
            });

        var result = await CreateService(source, repository.Object).ImportLatestAsync();

        result.ManifestsProcessed.Should().Be(1);
        repository.Verify(item => item.SaveExportByServiceAsync(
            new DateTime(2026, 8, 31),
            SubscriptionOne,
            It.IsAny<IReadOnlyCollection<CostByServiceRow>>(),
            CostExportPublicationModes.Shadow,
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.SaveImportRunCheckpointAsync(
            It.Is<CostExportImportCheckpoint>(checkpoint =>
                checkpoint.RunId == "run-historical"),
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.UpdateLatestImportCheckpointAsync(
            It.IsAny<CostExportImportCheckpoint>(),
            It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(item => item.SavePublicationWatermarkAsync(
            It.IsAny<CostExportPublicationWatermark>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ImportLatestAsync_ShouldDecompressGzipPartition()
    {
        var manifestName = ManifestName(SubscriptionOne, "run-gzip");
        var partition = PartitionName(SubscriptionOne, "run-gzip", 1) + ".gz";
        var source = new FakeExportSource(
            [manifestName],
            new Dictionary<string, string>
            {
                [manifestName] = ManifestJson(
                    SubscriptionOne,
                    "run-gzip",
                    "2026-08-19T19:30:53Z",
                    [(partition, 1L)],
                    compressionMode: "gzip")
            },
            new Dictionary<string, byte[]>
            {
                [partition] = Gzip(Csv(SubscriptionOne, "2026-08-18", "Storage", "stg", "1"))
            });
        var repository = CreateRepository();

        var result = await CreateService(source, repository.Object).ImportLatestAsync();

        result.ImportedRows.Should().Be(1);
    }

    [Fact]
    public async Task ImportLatestAsync_WhenOneSubscriptionFails_ShouldCompleteOtherAndAggregateFailure()
    {
        var failedManifest = ManifestName(SubscriptionOne, "run-failed");
        var validManifest = ManifestName(SubscriptionTwo, "run-valid");
        var failedPartition = PartitionName(SubscriptionOne, "run-failed", 1);
        var validPartition = PartitionName(SubscriptionTwo, "run-valid", 1);
        var source = new FakeExportSource(
            [failedManifest, validManifest],
            new Dictionary<string, string>
            {
                [failedManifest] = ManifestJson(
                    SubscriptionOne,
                    "run-failed",
                    "2026-08-19T10:00:00Z",
                    [(failedPartition, 1L)]),
                [validManifest] = ManifestJson(
                    SubscriptionTwo,
                    "run-valid",
                    "2026-08-19T11:00:00Z",
                    [(validPartition, 1L)])
            },
            new Dictionary<string, byte[]>
            {
                [failedPartition] = Utf8(Csv(SubscriptionOne, "invalid", "Storage", "stg", "1")),
                [validPartition] = Utf8(Csv(SubscriptionTwo, "2026-08-18", "Compute", "vm", "2"))
            });
        var repository = CreateRepository();
        var completedRuns = new List<string>();
        repository
            .Setup(item => item.SaveImportRunCheckpointAsync(
                It.IsAny<CostExportImportCheckpoint>(),
                It.IsAny<CancellationToken>()))
            .Callback<CostExportImportCheckpoint, CancellationToken>(
                (checkpoint, _) => completedRuns.Add(checkpoint.RunId))
            .Returns(Task.CompletedTask);

        var act = () => CreateService(
            source,
            repository.Object,
            Settings($"{SubscriptionOne},{SubscriptionTwo}")).ImportLatestAsync();

        var exception = await act.Should().ThrowAsync<CostExportImportException>();
        exception.Which.Result.ManifestsProcessed.Should().Be(1);
        exception.Which.Result.Failures.Should().ContainSingle();
        completedRuns.Should().Equal("run-valid");
    }

    [Fact]
    public async Task ImportLatestAsync_ShouldUseShadowUntilSubscriptionIsExplicitlyCutOver()
    {
        var manifestName = ManifestName(SubscriptionOne, "run-cutover");
        var partition = PartitionName(SubscriptionOne, "run-cutover", 1);
        var source = new FakeExportSource(
            [manifestName],
            new Dictionary<string, string>
            {
                [manifestName] = ManifestJson(
                    SubscriptionOne,
                    "run-cutover",
                    "2026-08-20T10:00:00Z",
                    [(partition, 1L)])
            },
            new Dictionary<string, byte[]>
            {
                [partition] = Utf8(Csv(SubscriptionOne, "2026-08-18", "Storage", "stg", "1"))
            });
        var repository = CreateRepository();
        string? mode = null;
        repository
            .Setup(item => item.SaveExportByServiceAsync(
                It.IsAny<DateTime>(),
                SubscriptionOne,
                It.IsAny<IReadOnlyCollection<CostByServiceRow>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<DateTime, string, IReadOnlyCollection<CostByServiceRow>, string, CancellationToken>(
                (_, _, _, value, _) => mode = value)
            .Returns(Task.CompletedTask);

        await CreateService(
            source,
            repository.Object,
            Settings(SubscriptionOne, SubscriptionOne)).ImportLatestAsync();

        mode.Should().Be(CostExportPublicationModes.Cutover);
    }

    [Fact]
    public async Task ImportLatestAsync_ShouldImportAllResourcesAndTrackUnallocatedCost()
    {
        var manifestName = ManifestName(SubscriptionOne, "run-filter");
        var partition = PartitionName(SubscriptionOne, "run-filter", 1);
        var csv = $"""
        UsageDateTime,SubscriptionGuid,ServiceName,InstanceId,PreTaxCost,Currency,ResourceGroup,ResourceLocation,ResourceType,Tags
        2026-08-18,{SubscriptionOne},Azure App Service,/subscriptions/{SubscriptionOne}/resources/app,1,USD,rg-app,brazilsouth,Microsoft.Web/sites,env=prod
        2026-08-18,{SubscriptionOne},Storage,/subscriptions/{SubscriptionOne}/resources/stg,2,USD,rg-data,eastus,Microsoft.Storage/storageAccounts,
        2026-08-18,{SubscriptionOne},Support,,3,USD,,,,
        """;
        var source = new FakeExportSource(
            [manifestName],
            new Dictionary<string, string>
            {
                [manifestName] = ManifestJson(
                    SubscriptionOne,
                    "run-filter",
                    "2026-08-20T10:00:00Z",
                    [(partition, 3L)])
            },
            new Dictionary<string, byte[]> { [partition] = Utf8(csv) });
        var repository = CreateRepository();
        IReadOnlyCollection<CostByServiceRow>? byService = null;
        IReadOnlyCollection<CostByResourceRow>? byResource = null;
        IReadOnlyCollection<CostAllocationSummary>? allocation = null;
        repository
            .Setup(item => item.SaveExportByServiceAsync(
                It.IsAny<DateTime>(),
                SubscriptionOne,
                It.IsAny<IReadOnlyCollection<CostByServiceRow>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<DateTime, string, IReadOnlyCollection<CostByServiceRow>, string, CancellationToken>(
                (_, _, rows, _, _) => byService = rows)
            .Returns(Task.CompletedTask);
        repository
            .Setup(item => item.SaveExportAllocationAsync(
                It.IsAny<DateTime>(),
                SubscriptionOne,
                It.IsAny<IReadOnlyCollection<CostAllocationSummary>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<DateTime, string, IReadOnlyCollection<CostAllocationSummary>, string, CancellationToken>(
                (_, _, rows, _, _) => allocation = rows)
            .Returns(Task.CompletedTask);
        repository
            .Setup(item => item.SaveExportByResourceAsync(
                It.IsAny<DateTime>(),
                SubscriptionOne,
                It.IsAny<IReadOnlyCollection<CostByResourceRow>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<DateTime, string, IReadOnlyCollection<CostByResourceRow>, string, CancellationToken>(
                (_, _, rows, _, _) => byResource = rows)
            .Returns(Task.CompletedTask);

        await CreateService(
            source,
            repository.Object,
            Settings(SubscriptionOne, resourceService: "Azure App Service")).ImportLatestAsync();

        byService.Should().HaveCount(3);
        byService!.Select(row => row.Label).Should().BeEquivalentTo("Azure App Service", "Storage", "Support");
        byResource.Should().HaveCount(2);
        var resourceRows = byResource!;
        resourceRows.Select(row => row.ServiceName).Should().BeEquivalentTo("Azure App Service", "Storage");
        var appResource = resourceRows.Single(row => row.ServiceName == "Azure App Service");
        appResource.ResourceGroup.Should().Be("rg-app");
        appResource.ResourceLocation.Should().Be("brazilsouth");
        appResource.ResourceType.Should().Be("Microsoft.Web/sites");
        appResource.Tags.Should().Contain("prod");
        allocation.Should().ContainSingle();
        var allocationSummary = allocation!.Single();
        allocationSummary.TotalCost.Should().Be(6);
        allocationSummary.AllocatedResourceCost.Should().Be(3);
        allocationSummary.UnallocatedCost.Should().Be(3);
        allocationSummary.TotalCost.Should().Be(
            allocationSummary.AllocatedResourceCost + allocationSummary.UnallocatedCost);
    }

    [Fact]
    public async Task ImportLatestAsync_WhenLatestUpdateFails_ShouldReconcileExistingRunOnRetry()
    {
        var manifestName = ManifestName(SubscriptionOne, "run-retry");
        var partition = PartitionName(SubscriptionOne, "run-retry", 1);
        var source = new FakeExportSource(
            [manifestName],
            new Dictionary<string, string>
            {
                [manifestName] = ManifestJson(
                    SubscriptionOne,
                    "run-retry",
                    "2026-08-20T10:00:00Z",
                    [(partition, 1L)])
            },
            new Dictionary<string, byte[]>
            {
                [partition] = Utf8(Csv(SubscriptionOne, "2026-08-18", "Storage", "stg", "1"))
            });
        var repository = CreateRepository();
        CostExportImportCheckpoint? saved = null;
        repository
            .SetupSequence(item => item.LoadImportCheckpointAsync(
                SubscriptionOne,
                TargetExport,
                "run-retry",
                CostExportPublicationModes.Shadow,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostExportImportCheckpoint?)null)
            .ReturnsAsync(() => saved);
        repository
            .Setup(item => item.SaveImportRunCheckpointAsync(
                It.IsAny<CostExportImportCheckpoint>(),
                It.IsAny<CancellationToken>()))
            .Callback<CostExportImportCheckpoint, CancellationToken>((value, _) => saved = value)
            .Returns(Task.CompletedTask);
        repository
            .SetupSequence(item => item.UpdateLatestImportCheckpointAsync(
                It.IsAny<CostExportImportCheckpoint>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("latest indisponível"))
            .Returns(Task.CompletedTask);

        var service = CreateService(source, repository.Object);
        await FluentActions.Invoking(() => service.ImportLatestAsync())
            .Should().ThrowAsync<CostExportImportException>();
        var retry = await service.ImportLatestAsync();

        retry.ManifestsSkippedByCheckpoint.Should().Be(1);
        source.StreamReads.Should().ContainSingle();
        repository.Verify(item => item.SaveImportRunCheckpointAsync(
            It.IsAny<CostExportImportCheckpoint>(),
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.SavePublicationWatermarkAsync(
            It.IsAny<CostExportPublicationWatermark>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ImportLatestAsync_WhenPublicationFails_ShouldNotCreateControlDocuments()
    {
        var (source, _, _) = SingleRunSource("run-publication-failure");
        var repository = CreateRepository();
        repository
            .Setup(item => item.SaveExportByServiceAsync(
                It.IsAny<DateTime>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyCollection<CostByServiceRow>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("falha de publicação"));

        await FluentActions.Invoking(() => CreateService(source, repository.Object).ImportLatestAsync())
            .Should().ThrowAsync<CostExportImportException>();

        repository.Verify(item => item.SaveImportRunCheckpointAsync(
            It.IsAny<CostExportImportCheckpoint>(),
            It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(item => item.UpdateLatestImportCheckpointAsync(
            It.IsAny<CostExportImportCheckpoint>(),
            It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(item => item.SavePublicationWatermarkAsync(
            It.IsAny<CostExportPublicationWatermark>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ImportLatestAsync_WhenRunCheckpointFails_ShouldNotAdvanceLatestOrWatermark()
    {
        var (source, _, _) = SingleRunSource("run-checkpoint-failure");
        var repository = CreateRepository();
        repository
            .Setup(item => item.SaveImportRunCheckpointAsync(
                It.IsAny<CostExportImportCheckpoint>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("falha no checkpoint"));

        await FluentActions.Invoking(() => CreateService(source, repository.Object).ImportLatestAsync())
            .Should().ThrowAsync<CostExportImportException>();

        repository.Verify(item => item.UpdateLatestImportCheckpointAsync(
            It.IsAny<CostExportImportCheckpoint>(),
            It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(item => item.SavePublicationWatermarkAsync(
            It.IsAny<CostExportPublicationWatermark>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ImportLatestAsync_WhenWatermarkFails_ShouldLeaveRunCheckpointForRetry()
    {
        var (source, _, _) = SingleRunSource("run-watermark-failure");
        var repository = CreateRepository();
        CostExportImportCheckpoint? saved = null;
        repository
            .SetupSequence(item => item.LoadImportCheckpointAsync(
                SubscriptionOne,
                TargetExport,
                "run-watermark-failure",
                CostExportPublicationModes.Shadow,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostExportImportCheckpoint?)null)
            .ReturnsAsync(() => saved);
        repository
            .Setup(item => item.SaveImportRunCheckpointAsync(
                It.IsAny<CostExportImportCheckpoint>(),
                It.IsAny<CancellationToken>()))
            .Callback<CostExportImportCheckpoint, CancellationToken>((value, _) => saved = value)
            .Returns(Task.CompletedTask);
        repository
            .SetupSequence(item => item.SavePublicationWatermarkAsync(
                It.IsAny<CostExportPublicationWatermark>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("falha no watermark"))
            .Returns(Task.CompletedTask);

        var service = CreateService(source, repository.Object);
        await FluentActions.Invoking(() => service.ImportLatestAsync())
            .Should().ThrowAsync<CostExportImportException>();
        await service.ImportLatestAsync();

        source.StreamReads.Should().ContainSingle();
        repository.Verify(item => item.SaveImportRunCheckpointAsync(
            It.IsAny<CostExportImportCheckpoint>(),
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.UpdateLatestImportCheckpointAsync(
            It.IsAny<CostExportImportCheckpoint>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ImportLatestAsync_ShouldRespectConfiguredManifestLimit()
    {
        var first = SingleRunSource("run-limit-1");
        var second = SingleRunSource("run-limit-2");
        var source = new FakeExportSource(
            [first.ManifestName, second.ManifestName],
            new Dictionary<string, string>
            {
                [first.ManifestName] = first.Source.GetText(first.ManifestName),
                [second.ManifestName] = second.Source.GetText(second.ManifestName)
            },
            new Dictionary<string, byte[]>
            {
                [first.PartitionName] = first.Source.GetBytes(first.PartitionName),
                [second.PartitionName] = second.Source.GetBytes(second.PartitionName)
            });

        var result = await CreateService(
            source,
            CreateRepository().Object,
            Settings(SubscriptionOne, maximumManifests: 1)).ImportLatestAsync();

        result.ManifestsFound.Should().Be(1);
        result.ManifestsProcessed.Should().Be(1);
    }

    [Fact]
    public async Task ImportLatestAsync_WhenIngestionAllowlistIsEmpty_ShouldProcessNothing()
    {
        var singleRun = SingleRunSource("run-disabled");

        var result = await CreateService(
            singleRun.Source,
            CreateRepository().Object,
            Settings(string.Empty)).ImportLatestAsync();

        result.ManifestsFound.Should().Be(0);
        singleRun.Source.TextReads.Should().BeEmpty();
        singleRun.Source.StreamReads.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportLatestAsync_WhenImportAllIsEnabled_ShouldDiscoverSubscriptionsFromExportPrefixes()
    {
        var subscriptions = new[] { SubscriptionOne, SubscriptionTwo };
        var manifests = subscriptions.ToDictionary(
            subscription => ManifestName(subscription, $"run-{subscription[..4]}"),
            subscription => subscription);
        var texts = manifests.ToDictionary(
            pair => pair.Key,
            pair => ManifestJson(
                pair.Value,
                $"run-{pair.Value[..4]}",
                "2026-08-20T10:00:00Z",
                [(PartitionName(pair.Value, $"run-{pair.Value[..4]}", 1), 1L)]));
        var streams = manifests.ToDictionary(
            pair => PartitionName(pair.Value, $"run-{pair.Value[..4]}", 1),
            pair => Utf8(Csv(pair.Value, "2026-08-18", "Storage", "stg", "1")));
        var source = new FakeExportSource(manifests.Keys.ToList(), texts, streams);

        var result = await CreateService(
            source,
            CreateRepository().Object,
            Settings(string.Empty, importAll: true)).ImportLatestAsync();

        result.SubscriptionsProcessed.Should().Be(2);
        result.ManifestsProcessed.Should().Be(2);
        source.SubscriptionDiscoveryCalls.Should().Be(1);
        source.TextReads.Select(TrySubscriptionFromPath)
            .Should().BeEquivalentTo(subscriptions);
    }

    [Fact]
    public async Task ImportLatestAsync_WithLimitBelowSubscriptionCount_ShouldEventuallyVisitEverySubscription()
    {
        var subscriptions = new[] { SubscriptionOne, SubscriptionTwo, SubscriptionThree };
        var manifests = subscriptions.ToDictionary(
            subscription => ManifestName(subscription, $"run-{subscription[..4]}"),
            subscription => subscription);
        var texts = manifests.ToDictionary(
            pair => pair.Key,
            pair => ManifestJson(
                pair.Value,
                $"run-{pair.Value[..4]}",
                "2026-08-20T10:00:00Z",
                [(PartitionName(pair.Value, $"run-{pair.Value[..4]}", 1), 1L)]));
        var streams = manifests.ToDictionary(
            pair => PartitionName(pair.Value, $"run-{pair.Value[..4]}", 1),
            pair => Utf8(Csv(pair.Value, "2026-08-18", "Storage", "stg", "1")));
        var source = new FakeExportSource(manifests.Keys.ToList(), texts, streams);
        var repository = CreateRepository();
        CostExportImportCursor? cursor = null;
        repository.Setup(item => item.LoadImportCursorAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => cursor);
        repository.Setup(item => item.SaveImportCursorAsync(
                It.IsAny<CostExportImportCursor>(), It.IsAny<CancellationToken>()))
            .Callback<CostExportImportCursor, CancellationToken>((value, _) => cursor = value)
            .Returns(Task.CompletedTask);
        var configuration = Settings(string.Join(',', subscriptions), maximumManifests: 1);
        var service = CreateService(source, repository.Object, configuration);

        for (var run = 0; run < subscriptions.Length; run++)
        {
            var result = await service.ImportLatestAsync();
            result.ManifestsProcessed.Should().Be(1);
        }

        source.TextReads.Select(TrySubscriptionFromPath)
            .Should().BeEquivalentTo(subscriptions);
    }

    [Fact]
    public async Task ImportLatestAsync_WhenNewRunFinishesFirst_ShouldPreventOlderSnapshotOverwrite()
    {
        var oldRun = SingleRunSource("run-old", "2026-08-19T10:00:00Z", "10");
        var newRun = SingleRunSource("run-new", "2026-08-20T10:00:00Z", "20");
        oldRun.Source.OpenReadDelay = TimeSpan.FromMilliseconds(150);
        var repository = CreateRepository();
        var semaphore = new SemaphoreSlim(1, 1);
        CostExportImportCheckpoint? latest = null;
        var publishedTotals = new List<decimal>();
        repository.Setup(item => item.LoadLatestCompletedImportCheckpointAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => latest);
        repository.Setup(item => item.TryAcquireExportPublicationLeaseAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string _, DateTime? maxUsageDate, DateTimeOffset submittedTime, string runId, CancellationToken token) =>
                AcquireTestLeaseAsync(semaphore, () => latest, maxUsageDate, submittedTime, runId, token));
        repository.Setup(item => item.UpdateLatestImportCheckpointAsync(
                It.IsAny<CostExportImportCheckpoint>(), It.IsAny<CancellationToken>()))
            .Callback<CostExportImportCheckpoint, CancellationToken>((value, _) => latest = value)
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.SaveExportByServiceAsync(
                It.IsAny<DateTime>(),
                SubscriptionOne,
                It.IsAny<IReadOnlyCollection<CostByServiceRow>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<DateTime, string, IReadOnlyCollection<CostByServiceRow>, string, CancellationToken>(
                (_, _, rows, _, _) => publishedTotals.Add(rows.Sum(row => row.TotalCost)))
            .Returns(Task.CompletedTask);

        var oldTask = CreateService(oldRun.Source, repository.Object).ImportLatestAsync();
        await Task.Delay(25);
        var newTask = CreateService(newRun.Source, repository.Object).ImportLatestAsync();
        await Task.WhenAll(oldTask, newTask);

        latest.Should().NotBeNull();
        latest!.RunId.Should().Be("run-new");
        publishedTotals.Should().Equal(20m);
        oldTask.Result.ManifestsSkippedByCheckpoint.Should().Be(1);
    }

    [Fact]
    public void Checkpoint_ShouldDeserializeLegacyPayloadWithoutNewMetadata()
    {
        const string json = """
        {
          "subscriptionId": "00000000-0000-0000-0000-00000000a006",
          "exportName": "finops-actual-cost-current-billing",
          "runId": "legacy-run",
          "submittedTime": "2026-08-19T10:00:00Z",
          "partitionsProcessed": 1,
          "rowsProcessed": 10,
          "status": "Completed"
        }
        """;

        var checkpoint = JsonSerializer.Deserialize<CostExportImportCheckpoint>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        checkpoint.Should().NotBeNull();
        checkpoint!.RunId.Should().Be("legacy-run");
        checkpoint.Source.Should().Be(CostDataSources.CostManagementExport);
        checkpoint.PublicationMode.Should().Be(CostExportPublicationModes.Cutover);
        checkpoint.MinUsageDate.Should().BeNull();
        checkpoint.MaxUsageDate.Should().BeNull();
        checkpoint.TotalsByCurrency.Should().BeEmpty();
    }

    private static Mock<ICostStorageRepository> CreateRepository(bool checkpointCompleted = false)
    {
        var repository = new Mock<ICostStorageRepository>();
        repository
            .Setup(item => item.LoadImportCheckpointAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(checkpointCompleted
                ? new CostExportImportCheckpoint { Status = "Completed" }
                : null);
        repository
            .Setup(item => item.LoadLatestCompletedImportCheckpointAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostExportImportCheckpoint?)null);
        repository
            .Setup(item => item.TryAcquireExportPublicationLeaseAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NoopAsyncDisposable());
        repository
            .Setup(item => item.LoadImportCursorAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostExportImportCursor?)null);
        repository
            .Setup(item => item.SaveImportCursorAsync(
                It.IsAny<CostExportImportCursor>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository
            .Setup(item => item.SaveExportByServiceAsync(
                It.IsAny<DateTime>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyCollection<CostByServiceRow>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository
            .Setup(item => item.SaveExportByResourceAsync(
                It.IsAny<DateTime>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyCollection<CostByResourceRow>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository
            .Setup(item => item.SaveImportRunCheckpointAsync(
                It.IsAny<CostExportImportCheckpoint>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository
            .Setup(item => item.SaveExportAllocationAsync(
                It.IsAny<DateTime>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyCollection<CostAllocationSummary>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository
            .Setup(item => item.UpdateLatestImportCheckpointAsync(
                It.IsAny<CostExportImportCheckpoint>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository
            .Setup(item => item.SavePublicationWatermarkAsync(
                It.IsAny<CostExportPublicationWatermark>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return repository;
    }

    private static CostExportImportService CreateService(
        ICostExportBlobSource source,
        ICostStorageRepository repository,
        IConfiguration? configuration = null,
        ICostExportVersionStore? versions = null)
    {
        var defaultVersions = new Mock<ICostExportVersionStore>();
        defaultVersions.Setup(v => v.HasPublishedRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return new CostExportImportService(
            source,
            repository,
            versions ?? defaultVersions.Object,
            new CostExportCsvParser(),
            new CostExportManifestParser(),
            configuration ?? Settings(SubscriptionOne),
            new NullLogger<CostExportImportService>());
    }

    private static IConfiguration Settings(
        string? allowlist,
        string? cutover = null,
        string? resourceService = null,
        int maximumManifests = 100,
        bool importAll = false)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["COST_EXPORT_SUBSCRIPTIONS"] = allowlist,
                ["COST_EXPORT_IMPORT_ALL_SUBSCRIPTIONS"] = importAll.ToString(),
                ["COST_EXPORT_CUTOVER_SUBSCRIPTIONS"] = cutover,
                ["EnableCostExportImport"] = "true",
                ["COST_RESOURCE_SERVICE"] = resourceService,
                ["COST_EXPORT_MAX_MANIFESTS_PER_RUN"] = maximumManifests.ToString()
            })
            .Build();
    }

    private static string ManifestName(
        string subscriptionId,
        string runId,
        string billingPeriod = "20260801-20260831")
    {
        return $"subscriptions/{subscriptionId}/billing-period/current/{TargetExport}/{billingPeriod}/{runId}/manifest.json";
    }

    private static (FakeExportSource Source, string ManifestName, string PartitionName) SingleRunSource(
        string runId,
        string submittedTime = "2026-08-20T10:00:00Z",
        string cost = "1")
    {
        var manifestName = ManifestName(SubscriptionOne, runId);
        var partitionName = PartitionName(SubscriptionOne, runId, 1);
        var source = new FakeExportSource(
            [manifestName],
            new Dictionary<string, string>
            {
                [manifestName] = ManifestJson(
                    SubscriptionOne,
                    runId,
                    submittedTime,
                    [(partitionName, 1L)])
            },
            new Dictionary<string, byte[]>
            {
                [partitionName] = Utf8(Csv(SubscriptionOne, "2026-08-18", "Storage", "stg", cost))
            });
        return (source, manifestName, partitionName);
    }

    private static string PartitionName(
        string subscriptionId,
        string runId,
        int index,
        string billingPeriod = "20260801-20260831")
    {
        return $"subscriptions/{subscriptionId}/billing-period/current/{TargetExport}/{billingPeriod}/{runId}/part_0_{index:0000}.csv";
    }

    private static string ManifestJson(
        string subscriptionId,
        string runId,
        string submittedTime,
        IReadOnlyList<(string Name, long Rows)> partitions,
        int? blobCount = null,
        long? dataRowCount = null,
        string compressionMode = "None",
        string exportType = "Usage",
        string timeframe = "MonthToDate",
        string? startDate = null,
        string? endDate = null)
    {
        return JsonSerializer.Serialize(new
        {
            manifestVersion = "2024-04-01",
            blobCount = blobCount ?? partitions.Count,
            dataRowCount = dataRowCount ?? partitions.Sum(partition => partition.Rows),
            exportConfig = new
            {
                exportName = TargetExport,
                type = exportType,
                timeFrame = timeframe,
                granularity = "Daily"
            },
            deliveryConfig = new
            {
                partitionData = true,
                dataOverwriteBehavior = "OverwritePreviousReport",
                fileFormat = "Csv",
                compressionMode,
                rootFolderPath = $"subscriptions/{subscriptionId}/billing-period/current"
            },
            runInfo = new
            {
                runId,
                submittedTime,
                startDate,
                endDate
            },
            blobs = partitions.Select(partition => new
            {
                blobName = partition.Name,
                dataRowCount = partition.Rows
            })
        });
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string TrySubscriptionFromPath(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries)[1];

    private static async Task<IAsyncDisposable?> AcquireTestLeaseAsync(
        SemaphoreSlim semaphore,
        Func<CostExportImportCheckpoint?> latest,
        DateTime? maxUsageDate,
        DateTimeOffset submittedTime,
        string runId,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        var checkpoint = latest();
        if (checkpoint is not null && CostStorageRepository.CompareControlDocuments(
                maxUsageDate,
                submittedTime,
                runId,
                checkpoint.MaxUsageDate,
                checkpoint.SubmittedTime,
                checkpoint.RunId) < 0)
        {
            semaphore.Release();
            return null;
        }

        return new CallbackAsyncDisposable(() => semaphore.Release());
    }

    private sealed class CallbackAsyncDisposable : IAsyncDisposable
    {
        private Action? _dispose;

        public CallbackAsyncDisposable(Action dispose) => _dispose = dispose;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private static string Csv(
        string subscriptionId,
        string date,
        string service,
        string resource,
        string cost)
    {
        return $"""
        UsageDateTime,SubscriptionGuid,ServiceName,InstanceId,PreTaxCost,Currency
        {date},{subscriptionId},{service},/subscriptions/{subscriptionId}/resources/{resource},{cost},USD
        """;
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static byte[] Gzip(string value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(Utf8(value));
        }

        return output.ToArray();
    }

    private sealed class FakeExportSource : ICostExportBlobSource
    {
        private readonly IReadOnlyList<string> _manifestNames;
        private readonly IReadOnlyDictionary<string, string> _texts;
        private readonly IReadOnlyDictionary<string, byte[]> _streams;

        public FakeExportSource(
            IReadOnlyList<string> manifestNames,
            IReadOnlyDictionary<string, string> texts,
            IReadOnlyDictionary<string, byte[]> streams)
        {
            _manifestNames = manifestNames;
            _texts = texts;
            _streams = streams;
        }

        public List<string> TextReads { get; } = [];
        public List<string> StreamReads { get; } = [];
        public int SubscriptionDiscoveryCalls { get; private set; }
        public TimeSpan OpenReadDelay { get; set; }

        public string GetText(string blobName) => _texts[blobName];

        public byte[] GetBytes(string blobName) => _streams[blobName];

        public async IAsyncEnumerable<string> ListSubscriptionIdsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SubscriptionDiscoveryCalls++;
            foreach (var subscriptionId in _manifestNames
                         .Select(TrySubscriptionFromPath)
                         .Where(subscriptionId => !string.IsNullOrWhiteSpace(subscriptionId))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return subscriptionId;
                await Task.Yield();
            }
        }

        public async IAsyncEnumerable<string> ListManifestNamesAsync(
            IReadOnlyCollection<string> subscriptionIds,
            int maximumResults,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var orderedManifests = subscriptionIds.SelectMany(subscriptionId =>
                _manifestNames.Where(name => name.StartsWith(
                    $"subscriptions/{subscriptionId}/billing-period/current/",
                    StringComparison.OrdinalIgnoreCase)));
            foreach (var manifestName in orderedManifests.Take(maximumResults))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return manifestName;
                await Task.Yield();
            }
        }

        public Task<string> ReadTextAsync(
            string blobName,
            CancellationToken cancellationToken = default)
        {
            TextReads.Add(blobName);
            return Task.FromResult(_texts[blobName]);
        }

        public async Task<Stream> OpenReadAsync(
            string blobName,
            CancellationToken cancellationToken = default)
        {
            StreamReads.Add(blobName);
            if (OpenReadDelay > TimeSpan.Zero)
            {
                await Task.Delay(OpenReadDelay, cancellationToken);
            }

            return new MemoryStream(_streams[blobName]);
        }
    }
}
