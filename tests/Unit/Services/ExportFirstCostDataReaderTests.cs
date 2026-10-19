using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public sealed class ExportFirstCostDataReaderTests
{
    private const string SubscriptionId = "00000000-0000-0000-0000-00000000a006";
    private static readonly DateTime StartDate = new(2026, 8, 16);
    private static readonly DateTime EndDate = new(2026, 8, 18);

    [Fact]
    public async Task ReadDailyCostsAsync_WhenSubscriptionIsIngestionOnly_ShouldUseShadowExportFirst()
    {
        var repository = RepositoryWithWatermark(CostExportPublicationModes.Shadow);
        SetupExportRows(repository, CostExportPublicationModes.Shadow);
        var queryApi = new Mock<ICostManagementClient>(MockBehavior.Strict);
        var sut = CreateReader(repository.Object, queryApi.Object, Settings());

        var result = await sut.ReadDailyCostsAsync(SubscriptionId, StartDate, EndDate);

        result.Source.Should().Be(CostDataSources.CostManagementExport);
        result.UsedFallback.Should().BeFalse();
        result.DataAsOf.Should().Be(EndDate);
        result.Entries.Should().HaveCount(3);
        result.Entries.Should().OnlyContain(entry =>
            entry.Source == CostDataSources.CostManagementExport && entry.Currency == "BRL");
        result.Entries.Single(entry => entry.Date == "2026-08-18").TotalCost.Should().Be(18m);
        queryApi.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadDailyCostsAsync_WhenSubscriptionIsCutover_ShouldReadOfficialExportPath()
    {
        var repository = RepositoryWithWatermark(CostExportPublicationModes.Cutover);
        SetupExportRows(repository, CostExportPublicationModes.Cutover);
        var queryApi = new Mock<ICostManagementClient>(MockBehavior.Strict);
        var sut = CreateReader(repository.Object, queryApi.Object, Settings(cutover: SubscriptionId));

        var result = await sut.ReadDailyCostsAsync(SubscriptionId, StartDate, EndDate);

        result.Source.Should().Be(CostDataSources.CostManagementExport);
        repository.Verify(item => item.LoadExportByServiceAsync(
            It.IsAny<DateTime>(),
            SubscriptionId,
            CostExportPublicationModes.Cutover,
            It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task ReadDailyCostsAsync_WhenWatermarkIsMissing_ShouldFallbackToQueryApi()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        repository.Setup(item => item.LoadPublicationWatermarkAsync(
                SubscriptionId,
                CostExportPublicationModes.Shadow,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostExportPublicationWatermark?)null);
        var queryApi = QueryApiWithRows();
        var sut = CreateReader(repository.Object, queryApi.Object, Settings());

        var result = await sut.ReadDailyCostsAsync(SubscriptionId, StartDate, EndDate);

        result.Source.Should().Be(CostDataSources.CostManagementQueryApi);
        result.UsedFallback.Should().BeTrue();
        result.FallbackReason.Should().Contain("Watermark");
        result.Entries.Should().ContainSingle(entry => entry.TotalCost == 30m);
    }

    [Fact]
    public async Task ReadDailyCostsAsync_WhenSubscriptionIsNotAllowlisted_ShouldUseQueryWithoutFallbackFlag()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        var queryApi = QueryApiWithRows();
        var sut = CreateReader(
            repository.Object,
            queryApi.Object,
            Settings(ingestion: "another-subscription"));

        var result = await sut.ReadDailyCostsAsync(SubscriptionId, StartDate, EndDate);

        result.Source.Should().Be(CostDataSources.CostManagementQueryApi);
        result.UsedFallback.Should().BeFalse();
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadDailyCostsAsync_WhenImportIsDisabled_ShouldKeepQueryApiAsPrimary()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        var queryApi = QueryApiWithRows();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableCostExportImport"] = "false",
                ["COST_EXPORT_IMPORT_ALL_SUBSCRIPTIONS"] = "true",
                ["COST_EXPORT_SUBSCRIPTIONS"] = SubscriptionId
            })
            .Build();
        var sut = CreateReader(repository.Object, queryApi.Object, configuration);

        var result = await sut.ReadDailyCostsAsync(SubscriptionId, StartDate, EndDate);

        result.Source.Should().Be(CostDataSources.CostManagementQueryApi);
        result.UsedFallback.Should().BeFalse();
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadDailyCostsAsync_WhenImportAllIsEnabled_ShouldUseExportWithoutAllowlist()
    {
        var repository = RepositoryWithWatermark(CostExportPublicationModes.Shadow);
        SetupExportRows(repository, CostExportPublicationModes.Shadow);
        var queryApi = new Mock<ICostManagementClient>(MockBehavior.Strict);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableCostExportImport"] = "true",
                ["COST_EXPORT_IMPORT_ALL_SUBSCRIPTIONS"] = "true",
                ["COST_EXPORT_SUBSCRIPTIONS"] = "",
                ["COST_EXPORT_CUTOVER_SUBSCRIPTIONS"] = ""
            })
            .Build();
        var sut = CreateReader(repository.Object, queryApi.Object, configuration);

        var result = await sut.ReadDailyCostsAsync(SubscriptionId, StartDate, EndDate);

        result.Source.Should().Be(CostDataSources.CostManagementExport);
        result.UsedFallback.Should().BeFalse();
        queryApi.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadDailyCostsAsync_WhenExportIsTooStale_ShouldFallbackToQueryApi()
    {
        var repository = RepositoryWithWatermark(
            CostExportPublicationModes.Shadow,
            maxUsageDate: new DateTime(2026, 8, 14));
        var queryApi = QueryApiWithRows();
        var sut = CreateReader(repository.Object, queryApi.Object, Settings(maxStalenessDays: 3));

        var result = await sut.ReadDailyCostsAsync(SubscriptionId, StartDate, EndDate);

        result.Source.Should().Be(CostDataSources.CostManagementQueryApi);
        result.UsedFallback.Should().BeTrue();
        result.FallbackReason.Should().Contain("defasado");
        result.Entries.Should().OnlyContain(entry => entry.UsedFallback &&
            entry.FallbackReason != null && entry.FallbackReason.Contains("defasado"));
    }

    [Fact]
    public async Task ReadDailyCostsAsync_WhenLatestManifestStartsAfterRequestedWindow_ShouldReadPersistedHistory()
    {
        var availableStart = new DateTime(2026, 8, 18);
        var repository = RepositoryWithWatermark(
            CostExportPublicationModes.Shadow,
            minUsageDate: availableStart);
        SetupExportRows(repository, CostExportPublicationModes.Shadow);
        var queryApi = new Mock<ICostManagementClient>(MockBehavior.Strict);
        var sut = CreateReader(repository.Object, queryApi.Object, Settings());

        var result = await sut.ReadDailyCostsAsync(SubscriptionId, StartDate, EndDate);

        result.Source.Should().Be(CostDataSources.CostManagementExport);
        result.UsedFallback.Should().BeFalse();
        result.Entries.Should().HaveCount(3);
        result.Entries.Should().Contain(entry =>
            entry.Date == "2026-08-16" && entry.TotalCost == 16m);
        result.Entries.Should().Contain(entry =>
            entry.Date == "2026-08-18" && entry.TotalCost == 18m);
        repository.Verify(item => item.LoadExportByServiceAsync(
            It.IsAny<DateTime>(),
            SubscriptionId,
            CostExportPublicationModes.Shadow,
            It.IsAny<CancellationToken>()), Times.Exactly(3));
        queryApi.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadDailyCostsAsync_WhenPersistedHistoryHasMissingDay_ShouldKeepAvailableExportRows()
    {
        var repository = RepositoryWithWatermark(CostExportPublicationModes.Shadow);
        repository.Setup(item => item.LoadExportByServiceAsync(
                It.IsAny<DateTime>(),
                SubscriptionId,
                CostExportPublicationModes.Shadow,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime date, string _, string _, CancellationToken _) =>
                date == StartDate.AddDays(1)
                    ? []
                    : [new CostByServiceRow { Label = "Storage", Currency = "BRL", TotalCost = date.Day }]);
        var queryApi = new Mock<ICostManagementClient>(MockBehavior.Strict);
        var sut = CreateReader(repository.Object, queryApi.Object, Settings());

        var result = await sut.ReadDailyCostsAsync(SubscriptionId, StartDate, EndDate);

        result.Source.Should().Be(CostDataSources.CostManagementExport);
        result.Entries.Should().HaveCount(2);
        result.Entries.Should().NotContain(entry => entry.Date == "2026-08-17");
        queryApi.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadDailyCostsAsync_WhenPersistedHistoryHasNoRows_ShouldFallbackToQueryApi()
    {
        var repository = RepositoryWithWatermark(
            CostExportPublicationModes.Shadow,
            minUsageDate: EndDate.AddDays(1),
            maxUsageDate: EndDate.AddDays(1));
        repository.Setup(item => item.LoadExportByServiceAsync(
                It.IsAny<DateTime>(),
                SubscriptionId,
                CostExportPublicationModes.Shadow,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var queryApi = QueryApiWithRows();
        var sut = CreateReader(repository.Object, queryApi.Object, Settings());

        var result = await sut.ReadDailyCostsAsync(SubscriptionId, StartDate, EndDate);

        result.Source.Should().Be(CostDataSources.CostManagementQueryApi);
        result.UsedFallback.Should().BeTrue();
        result.FallbackReason.Should().Contain("não retornou custos");
    }

    [Fact]
    public async Task ReadDailyCostsAsync_WhenExportContainsDifferentCurrencies_ShouldNeverSumThemAndFallback()
    {
        var repository = RepositoryWithWatermark(CostExportPublicationModes.Shadow);
        repository.Setup(item => item.LoadExportByServiceAsync(
                It.IsAny<DateTime>(),
                SubscriptionId,
                CostExportPublicationModes.Shadow,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime date, string _, string _, CancellationToken _) =>
            [
                new CostByServiceRow { Label = "Storage", Currency = "BRL", TotalCost = 10 },
                new CostByServiceRow { Label = "Compute", Currency = "USD", TotalCost = 20 }
            ]);
        var queryApi = QueryApiWithRows();
        var sut = CreateReader(repository.Object, queryApi.Object, Settings());

        var result = await sut.ReadDailyCostsAsync(SubscriptionId, StartDate, EndDate);

        result.Source.Should().Be(CostDataSources.CostManagementQueryApi);
        result.UsedFallback.Should().BeTrue();
        result.FallbackReason.Should().Contain("moedas diferentes");
        result.Entries.Should().ContainSingle(entry => entry.Currency == "BRL");
    }

    private static Mock<ICostStorageRepository> RepositoryWithWatermark(
        string publicationMode,
        DateTime? minUsageDate = null,
        DateTime? maxUsageDate = null)
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        repository.Setup(item => item.LoadPublicationWatermarkAsync(
                SubscriptionId,
                publicationMode,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportPublicationWatermark
            {
                SubscriptionId = SubscriptionId,
                PublicationMode = publicationMode,
                MinUsageDate = minUsageDate ?? StartDate,
                MaxUsageDate = maxUsageDate ?? EndDate,
                Status = "Completed"
            });
        return repository;
    }

    private static void SetupExportRows(
        Mock<ICostStorageRepository> repository,
        string publicationMode)
    {
        repository.Setup(item => item.LoadExportByServiceAsync(
                It.IsAny<DateTime>(),
                SubscriptionId,
                publicationMode,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime date, string _, string _, CancellationToken _) =>
            [
                new CostByServiceRow
                {
                    Label = "Storage",
                    Currency = "BRL",
                    TotalCost = date.Day
                }
            ]);
    }

    private static Mock<ICostManagementClient> QueryApiWithRows()
    {
        var queryApi = new Mock<ICostManagementClient>(MockBehavior.Strict);
        queryApi.Setup(item => item.QueryCostByServiceAsync(
                SubscriptionId,
                StartDate,
                EndDate,
                "Daily",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostByServiceQueryResponse
            {
                SubscriptionId = SubscriptionId,
                Rows =
                [
                    new CostByServiceQueryRecord
                    {
                        UsageDate = EndDate,
                        Label = "Storage",
                        Currency = "BRL",
                        TotalCost = 30
                    }
                ]
            });
        return queryApi;
    }

    private static ExportFirstCostDataReader CreateReader(
        ICostStorageRepository repository,
        ICostManagementClient queryApi,
        IConfiguration configuration,
        ICostExportVersionStore? versions = null)
    {
        return new ExportFirstCostDataReader(
            repository,
            versions ?? Mock.Of<ICostExportVersionStore>(),
            queryApi,
            configuration,
            new NullLogger<ExportFirstCostDataReader>());
    }

    [Fact]
    public async Task ReadDailyCostsAsync_WhenIndexIsPartial_ShouldReadMissingDatesFromLegacyStorage()
    {
        var repository = RepositoryWithWatermark(CostExportPublicationModes.Shadow);
        SetupExportRows(repository, CostExportPublicationModes.Shadow);
        var snapshot = new CostExportDailySnapshot
        {
            SubscriptionId = SubscriptionId,
            PublicationMode = CostExportPublicationModes.Shadow,
            Date = EndDate,
            ExportName = "export",
            RunId = "run-september",
            SubmittedTime = DateTimeOffset.UtcNow,
            ByService = [new CostByServiceRow { Label = "Storage", Currency = "BRL", TotalCost = 99 }]
        };
        var index = new CostExportRevisionIndex
        {
            SubscriptionId = SubscriptionId,
            PublicationMode = CostExportPublicationModes.Shadow,
            Days = new SortedDictionary<string, CostExportDayReference>
            {
                [EndDate.ToString("yyyy-MM-dd")] = new() { Revision = snapshot.Revision, RunId = snapshot.RunId }
            }
        };
        var versions = new Mock<ICostExportVersionStore>(MockBehavior.Strict);
        versions.Setup(v => v.LoadIndexAsync(SubscriptionId, CostExportPublicationModes.Shadow, It.IsAny<CancellationToken>()))
            .ReturnsAsync(index);
        versions.Setup(v => v.LoadSnapshotAsync(SubscriptionId, CostExportPublicationModes.Shadow, snapshot.Revision, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);

        var result = await CreateReader(repository.Object, Mock.Of<ICostManagementClient>(), Settings(), versions.Object)
            .ReadDailyCostsAsync(SubscriptionId, StartDate, EndDate);

        result.Entries.Should().Contain(e => e.Date == StartDate.ToString("yyyy-MM-dd")
            && e.Revision == "legacy-unversioned");
        result.Entries.Should().Contain(e => e.Date == EndDate.ToString("yyyy-MM-dd")
            && e.TotalCost == 99 && e.Revision == snapshot.Revision);
        repository.Verify(r => r.LoadExportByServiceAsync(EndDate, SubscriptionId,
            CostExportPublicationModes.Shadow, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReadDailyCostsAsync_WhenPartialIndexEndsBeforeWatermark_ShouldKeepNewerLegacyDates()
    {
        var repository = RepositoryWithWatermark(CostExportPublicationModes.Shadow);
        SetupExportRows(repository, CostExportPublicationModes.Shadow);
        var snapshot = new CostExportDailySnapshot
        {
            SubscriptionId = SubscriptionId,
            PublicationMode = CostExportPublicationModes.Shadow,
            Date = StartDate,
            ExportName = "export",
            RunId = "indexed-run",
            SubmittedTime = DateTimeOffset.UtcNow,
            ByService = [new CostByServiceRow { Label = "Storage", Currency = "BRL", TotalCost = 99 }]
        };
        var index = new CostExportRevisionIndex
        {
            SubscriptionId = SubscriptionId,
            PublicationMode = CostExportPublicationModes.Shadow,
            Days = new SortedDictionary<string, CostExportDayReference>
            {
                [StartDate.ToString("yyyy-MM-dd")] = new()
                {
                    Revision = snapshot.Revision,
                    RunId = snapshot.RunId,
                    ImportedAt = DateTimeOffset.UtcNow
                }
            }
        };
        var versions = new Mock<ICostExportVersionStore>(MockBehavior.Strict);
        versions.Setup(v => v.LoadIndexAsync(SubscriptionId, CostExportPublicationModes.Shadow, It.IsAny<CancellationToken>()))
            .ReturnsAsync(index);
        versions.Setup(v => v.LoadSnapshotAsync(SubscriptionId, CostExportPublicationModes.Shadow, snapshot.Revision, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);

        var result = await CreateReader(repository.Object, Mock.Of<ICostManagementClient>(), Settings(), versions.Object)
            .ReadDailyCostsAsync(SubscriptionId, StartDate, EndDate);

        result.DataAsOf.Should().Be(EndDate);
        result.Entries.Should().Contain(entry => entry.Date == StartDate.ToString("yyyy-MM-dd")
            && entry.TotalCost == 99m && entry.Revision == snapshot.Revision);
        result.Entries.Should().Contain(entry => entry.Date == EndDate.ToString("yyyy-MM-dd")
            && entry.Revision == "legacy-unversioned");
    }

    [Fact]
    public async Task ReadDailyCostsAsync_WhenVersionIsTombstone_ShouldNotRestoreLegacyCost()
    {
        var repository = RepositoryWithWatermark(CostExportPublicationModes.Shadow,
            minUsageDate: EndDate, maxUsageDate: EndDate);
        SetupExportRows(repository, CostExportPublicationModes.Shadow);
        var snapshot = new CostExportDailySnapshot
        {
            SubscriptionId = SubscriptionId, PublicationMode = CostExportPublicationModes.Shadow,
            Date = EndDate, ExportName = "export", RunId = "correction", IsTombstone = true, TombstoneCurrency = "BRL",
            SubmittedTime = DateTimeOffset.UtcNow
        };
        var index = new CostExportRevisionIndex
        {
            SubscriptionId = SubscriptionId, PublicationMode = CostExportPublicationModes.Shadow,
            Days = new SortedDictionary<string, CostExportDayReference>
            {
                [EndDate.ToString("yyyy-MM-dd")] = new() { Revision = snapshot.Revision, RunId = snapshot.RunId }
            }
        };
        var versions = new Mock<ICostExportVersionStore>(MockBehavior.Strict);
        versions.Setup(v => v.LoadIndexAsync(SubscriptionId, CostExportPublicationModes.Shadow, It.IsAny<CancellationToken>()))
            .ReturnsAsync(index);
        versions.Setup(v => v.LoadSnapshotAsync(SubscriptionId, CostExportPublicationModes.Shadow, snapshot.Revision, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        var query = new Mock<ICostManagementClient>(MockBehavior.Strict);

        var result = await CreateReader(repository.Object, query.Object, Settings(), versions.Object)
            .ReadDailyCostsAsync(SubscriptionId, EndDate, EndDate);

        result.Source.Should().Be(CostDataSources.CostManagementExport);
        result.Entries.Should().ContainSingle(e => e.TotalCost == 0 && e.Revision == snapshot.Revision);
        repository.Verify(r => r.LoadExportByServiceAsync(It.IsAny<DateTime>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReadResourceCostsAsync_WhenVersionIsTombstone_ShouldReturnAuthoritativeEmptySnapshot()
    {
        var repository = RepositoryWithWatermark(CostExportPublicationModes.Shadow,
            minUsageDate: EndDate, maxUsageDate: EndDate);
        SetupResourceRows(repository, CostExportPublicationModes.Shadow);
        var snapshot = new CostExportDailySnapshot
        {
            SubscriptionId = SubscriptionId,
            PublicationMode = CostExportPublicationModes.Shadow,
            Date = EndDate,
            ExportName = "export",
            RunId = "correction",
            IsTombstone = true,
            TombstoneCurrency = "BRL",
            SubmittedTime = DateTimeOffset.UtcNow
        };
        var index = new CostExportRevisionIndex
        {
            SubscriptionId = SubscriptionId,
            PublicationMode = CostExportPublicationModes.Shadow,
            Days = new SortedDictionary<string, CostExportDayReference>
            {
                [EndDate.ToString("yyyy-MM-dd")] = new() { Revision = snapshot.Revision, RunId = snapshot.RunId }
            }
        };
        var versions = new Mock<ICostExportVersionStore>(MockBehavior.Strict);
        versions.Setup(v => v.LoadIndexAsync(SubscriptionId, CostExportPublicationModes.Shadow, It.IsAny<CancellationToken>()))
            .ReturnsAsync(index);
        versions.Setup(v => v.LoadSnapshotAsync(SubscriptionId, CostExportPublicationModes.Shadow, snapshot.Revision, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        var query = new Mock<ICostManagementClient>(MockBehavior.Strict);

        var result = await CreateReader(repository.Object, query.Object, Settings(), versions.Object)
            .ReadResourceCostsAsync(SubscriptionId, EndDate, EndDate);

        result.Rows.Should().BeEmpty();
        result.Provenance.Source.Should().Be(CostDataSources.CostManagementExport);
        result.Provenance.DataAsOf.Should().Be(EndDate);
        result.Provenance.AvailableDates.Should().Equal(EndDate);
        repository.Verify(r => r.LoadExportByResourceAsync(It.IsAny<DateTime>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        query.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadResourceCosts_ShouldUseExportWithoutCallingQueryOrDimensions(bool cutover)
    {
        var mode = cutover ? CostExportPublicationModes.Cutover : CostExportPublicationModes.Shadow;
        var repository = RepositoryWithWatermark(mode);
        SetupResourceRows(repository, mode);
        var query = new Mock<ICostManagementClient>(MockBehavior.Strict);
        var reader = CreateReader(repository.Object, query.Object, Settings(cutover: cutover ? SubscriptionId : null));
        var result = await reader.ReadResourceCostsAsync(SubscriptionId, StartDate, EndDate);
        result.Provenance.Source.Should().Be(CostDataSources.CostManagementExport);
        result.Provenance.PublicationMode.Should().Be(mode);
        result.Provenance.UsedFallback.Should().BeFalse();
        result.Provenance.DataAsOf.Should().Be(EndDate);
        result.Provenance.AvailableDates.Should().HaveCount(3);
        result.Rows.Should().HaveCount(3);
        result.Rows.Should().OnlyContain(r => r.ResourceType == "Microsoft.Sql/servers/databases" && r.ResourceGroupName == "rg");
        query.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadResourceCosts_ShouldKeepZeroCreditsAndHistoricalRowsBeforeLatestManifest()
    {
        var repository = RepositoryWithWatermark(CostExportPublicationModes.Shadow, minUsageDate: EndDate);
        SetupResourceRows(repository, CostExportPublicationModes.Shadow, date => date == StartDate ? -5 : 0);
        var query = new Mock<ICostManagementClient>(MockBehavior.Strict);
        var reader = CreateReader(repository.Object, query.Object, Settings());
        var result = await reader.ReadResourceCostsAsync(SubscriptionId, StartDate, EndDate);
        result.Rows.Sum(r => r.TotalCost).Should().Be(-5);
        result.Rows.Should().HaveCount(3);
        result.Provenance.AvailableDates.Should().Contain(StartDate);
        query.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadResourceCosts_ShouldNotInferMissingDaysAsZeroOrUseWatermarkAsObservedDate()
    {
        var repository = RepositoryWithWatermark(CostExportPublicationModes.Shadow, maxUsageDate: EndDate.AddDays(5));
        repository.Setup(r => r.LoadExportByResourceAsync(It.IsAny<DateTime>(), SubscriptionId,
                CostExportPublicationModes.Shadow, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime date, string _, string _, CancellationToken _) => date == StartDate
                ? new List<CostByResourceRow> { new() { ResourceId = "resource", TotalCost = 0 } } : new());
        var reader = CreateReader(repository.Object, Mock.Of<ICostManagementClient>(), Settings());
        var result = await reader.ReadResourceCostsAsync(SubscriptionId, StartDate, EndDate);
        result.Provenance.DataAsOf.Should().Be(StartDate);
        result.Provenance.AvailableDates.Should().Equal(StartDate);
        result.Rows.Should().ContainSingle();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("empty")]
    [InlineData("failure")]
    [InlineData("staleRows")]
    public async Task ReadResourceCosts_WhenExportIsUnavailable_ShouldUseSingleQueryFallback(string scenario)
    {
        var mode = CostExportPublicationModes.Shadow;
        var repository = RepositoryWithWatermark(mode, maxUsageDate: scenario == "stale" ? StartDate.AddDays(-5) : EndDate);
        if (scenario == "missing")
            repository.Setup(r => r.LoadPublicationWatermarkAsync(SubscriptionId, mode, It.IsAny<CancellationToken>()))
                .ReturnsAsync((CostExportPublicationWatermark?)null);
        if (scenario == "empty")
            repository.Setup(r => r.LoadExportByResourceAsync(It.IsAny<DateTime>(), SubscriptionId, mode, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<CostByResourceRow>());
        if (scenario == "failure")
            repository.Setup(r => r.LoadExportByResourceAsync(It.IsAny<DateTime>(), SubscriptionId, mode, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("storage unavailable"));
        if (scenario == "staleRows")
            repository.Setup(r => r.LoadExportByResourceAsync(It.IsAny<DateTime>(), SubscriptionId, mode, It.IsAny<CancellationToken>()))
                .ReturnsAsync((DateTime date, string _, string _, CancellationToken _) => date == StartDate
                    ? new List<CostByResourceRow> { new() { ResourceId = "resource", TotalCost = 12 } } : new());
        var query = ResourceQuery();
        var result = await CreateReader(repository.Object, query.Object, Settings(maxStalenessDays: scenario == "staleRows" ? 0 : 3))
            .ReadResourceCostsAsync(SubscriptionId, StartDate, EndDate);
        result.Provenance.UsedFallback.Should().BeTrue();
        result.Provenance.Source.Should().Be(CostDataSources.CostManagementQueryApi);
        result.Provenance.FallbackReason.Should().NotBeNullOrWhiteSpace();
        result.Provenance.ExportRunId.Should().BeNull();
        result.Rows.Should().ContainSingle();
        query.Verify(q => q.QueryCostDiagnosticsAsync(SubscriptionId, StartDate, EndDate, "Daily", It.IsAny<IReadOnlyCollection<string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReadResourceCosts_WhenNotAllowlisted_ShouldUseQueryWithoutFallbackFlag()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        var result = await CreateReader(repository.Object, ResourceQuery().Object, Settings(ingestion: "other"))
            .ReadResourceCostsAsync(SubscriptionId, StartDate, EndDate);
        result.Provenance.UsedFallback.Should().BeFalse();
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadResourceCosts_ShouldPropagateCancellationWithoutFallback()
    {
        var repository = RepositoryWithWatermark(CostExportPublicationModes.Shadow);
        repository.Setup(r => r.LoadExportByResourceAsync(It.IsAny<DateTime>(), SubscriptionId,
                CostExportPublicationModes.Shadow, It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        var query = new Mock<ICostManagementClient>(MockBehavior.Strict);
        var reader = CreateReader(repository.Object, query.Object, Settings());
        await reader.Invoking(r => r.ReadResourceCostsAsync(SubscriptionId, StartDate, EndDate)).Should().ThrowAsync<OperationCanceledException>();
        query.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadResourceCosts_ShouldRejectMixedQueryCurrenciesAndInvalidWindow()
    {
        var query = ResourceQuery();
        query.Setup(q => q.QueryCostDiagnosticsAsync(SubscriptionId, StartDate, EndDate, "Daily", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new CostDiagnosticRow { Currency = "USD", UsageDate = StartDate }, new CostDiagnosticRow { Currency = "BRL", UsageDate = EndDate } });
        var reader = CreateReader(Mock.Of<ICostStorageRepository>(), query.Object, Settings(ingestion: "other"));
        await reader.Invoking(r => r.ReadResourceCostsAsync(SubscriptionId, StartDate, EndDate)).Should().ThrowAsync<InvalidDataException>();
        await reader.Invoking(r => r.ReadResourceCostsAsync(SubscriptionId, EndDate, StartDate)).Should().ThrowAsync<ArgumentException>();
    }

    private static void SetupResourceRows(Mock<ICostStorageRepository> repository, string mode, Func<DateTime, decimal>? amount = null)
    {
        repository.Setup(r => r.LoadExportByResourceAsync(It.IsAny<DateTime>(), SubscriptionId, mode, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime date, string _, string _, CancellationToken _) => new List<CostByResourceRow>
            {
                new() { ResourceId = "resource", ResourceType = "Microsoft.Sql/servers/databases", ResourceGroup = "rg",
                    TotalCost = amount?.Invoke(date) ?? 12, Currency = "BRL", SubscriptionId = SubscriptionId }
            });
    }

    private static Mock<ICostManagementClient> ResourceQuery()
    {
        var query = new Mock<ICostManagementClient>(MockBehavior.Strict);
        query.Setup(q => q.QueryCostDiagnosticsAsync(SubscriptionId, StartDate, EndDate, "Daily", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new CostDiagnosticRow { ResourceId = "resource", UsageDate = EndDate, TotalCost = 10 } });
        return query;
    }

    private static IConfiguration Settings(
        string ingestion = SubscriptionId,
        string? cutover = null,
        int maxStalenessDays = 3)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableCostExportImport"] = "true",
                ["COST_EXPORT_SUBSCRIPTIONS"] = ingestion,
                ["COST_EXPORT_CUTOVER_SUBSCRIPTIONS"] = cutover,
                ["COST_EXPORT_ANOMALY_MAX_STALENESS_DAYS"] = maxStalenessDays.ToString()
            })
            .Build();
    }
}
