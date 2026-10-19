using System.Net;
using System.Text.Json;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Functions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Personal.FinOpsApi.AzureFunctions.UnitTests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Functions;

public sealed class CostApiFunctionTests
{
    [Fact]
    public async Task GetCostByServiceAsync_ShouldRequireReaderRole()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        var currentUser = User(new CurrentUserContext { IsAuthenticated = false });
        var sut = CreateFunction(repository.Object, currentUser.Object);

        var response = await sut.GetCostByServiceAsync(
            HttpTestHelpers.CreateGetRequest("https://localhost/api/cost/by-service"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetCostByServiceAsync_ShouldForbidAuthenticatedUserWithoutFinOpsRole()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        var currentUser = User(new CurrentUserContext { IsAuthenticated = true });
        var sut = CreateFunction(repository.Object, currentUser.Object);

        var response = await sut.GetCostByServiceAsync(
            HttpTestHelpers.CreateGetRequest("https://localhost/api/cost/by-service"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetCostByServiceAsync_ShouldKeepCurrenciesSeparateAndReturnWatermarkMetadata()
    {
        const string subscriptionId = "00000000-0000-0000-0000-00000000a006";
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        repository.Setup(item => item.LoadByServiceAsync(
                new DateTime(2026, 8, 18), subscriptionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CostByServiceRow { Label = "Storage", Currency = "USD", TotalCost = 10, SubscriptionId = subscriptionId },
                new CostByServiceRow { Label = "Storage", Currency = "BRL", TotalCost = 20, SubscriptionId = subscriptionId }
            ]);
        repository.Setup(item => item.LoadAllocationAsync(
                new DateTime(2026, 8, 18), subscriptionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CostAllocationSummary
                {
                    SubscriptionId = subscriptionId,
                    UsageDate = new DateTime(2026, 8, 18),
                    Currency = "USD",
                    TotalCost = 10,
                    AllocatedResourceCost = 7,
                    UnallocatedCost = 3
                }
            ]);
        repository.Setup(item => item.LoadPublicationWatermarkAsync(
                subscriptionId, CostExportPublicationModes.Cutover, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportPublicationWatermark
            {
                SubscriptionId = subscriptionId,
                Source = CostDataSources.CostManagementExport,
                MaxUsageDate = new DateTime(2026, 8, 18)
            });
        var sut = CreateFunction(repository.Object, UserWithReader().Object);

        var response = await sut.GetCostByServiceAsync(HttpTestHelpers.CreateGetRequest(
            $"https://localhost/api/cost/by-service?date=2026-08-18&subscription={subscriptionId}"));
        var json = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Get(json.RootElement, "source").GetString().Should().Be(CostDataSources.CostManagementExport);
        Get(json.RootElement, "dataAsOf").GetDateTime().Date.Should().Be(new DateTime(2026, 8, 18));
        Get(json.RootElement, "items").GetArrayLength().Should().Be(2);
        var allocation = Get(json.RootElement, "allocation")[0];
        Get(allocation, "totalCost").GetDecimal().Should().Be(
            Get(allocation, "allocatedResourceCost").GetDecimal() + Get(allocation, "unallocatedCost").GetDecimal());
    }

    [Fact]
    public async Task GetCostByResourceAsync_ShouldApplyOptionalFilters()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        repository.Setup(item => item.LoadByResourceAllAsync(
                new DateTime(2026, 8, 18), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CostByResourceRow
                {
                    ResourceId = "/subscriptions/sub/resourceGroups/rg-a/providers/Microsoft.Web/sites/app-a",
                    Label = "app-a", ServiceName = "Azure App Service", ResourceGroup = "rg-a",
                    ResourceLocation = "brazilsouth", ResourceType = "Microsoft.Web/sites", Currency = "USD",
                    TotalCost = 5, SubscriptionId = "sub"
                },
                new CostByResourceRow
                {
                    ResourceId = "/subscriptions/sub/resourceGroups/rg-b/providers/Microsoft.Storage/storageAccounts/stg",
                    Label = "stg", ServiceName = "Storage", ResourceGroup = "rg-b",
                    ResourceLocation = "eastus", ResourceType = "Microsoft.Storage/storageAccounts", Currency = "USD",
                    TotalCost = 8, SubscriptionId = "sub"
                }
            ]);
        // A busca pelo snapshot mais recente de cada assinatura olha os dias anteriores também.
        repository.Setup(item => item.LoadByResourceAllAsync(
                It.Is<DateTime>(value => value != new DateTime(2026, 8, 18)), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(item => item.LoadAllocationAllAsync(
                new DateTime(2026, 8, 18), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(item => item.LoadPublicationWatermarkAsync(
                "sub", CostExportPublicationModes.Cutover, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostExportPublicationWatermark?)null);
        var sut = CreateFunction(repository.Object, UserWithReader().Object);

        var response = await sut.GetCostByResourceAsync(HttpTestHelpers.CreateGetRequest(
            "https://localhost/api/cost/by-resource?date=2026-08-18&resourceGroup=rg-a&service=App%20Service&currency=USD"));
        var json = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = Get(json.RootElement, "items");
        items.GetArrayLength().Should().Be(1);
        Get(items[0], "label").GetString().Should().Be("app-a");
    }

    [Fact]
    public async Task GetCostByResourceAsync_WhenRequestedSnapshotIsMissing_ShouldReturnLatestLocalSnapshotWithItsDate()
    {
        const string subscriptionId = "sub";
        var requestedDate = new DateTime(2026, 8, 18);
        var snapshotDate = requestedDate.AddDays(-1);
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        repository.Setup(item => item.LoadByResourceAsync(requestedDate, subscriptionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(item => item.LoadByResourceAsync(snapshotDate, subscriptionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CostByResourceRow
                {
                    ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/serverFarms/plan",
                    Label = "plan", Currency = "BRL", TotalCost = 12, SubscriptionId = subscriptionId
                }
            ]);
        repository.Setup(item => item.LoadAllocationAsync(snapshotDate, subscriptionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(item => item.LoadPublicationWatermarkAsync(
                subscriptionId, CostExportPublicationModes.Cutover, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportPublicationWatermark
            {
                SubscriptionId = subscriptionId,
                Source = CostDataSources.CostManagementExport,
                MaxUsageDate = snapshotDate
            });
        var sut = CreateFunction(repository.Object, UserWithReader().Object);

        var response = await sut.GetCostByResourceAsync(HttpTestHelpers.CreateGetRequest(
            "https://localhost/api/cost/by-resource?date=2026-08-18&subscription=sub"));
        var json = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Get(json.RootElement, "requestedDate").GetDateTime().Date.Should().Be(requestedDate);
        Get(json.RootElement, "dataAsOf").GetDateTime().Date.Should().Be(snapshotDate);
        Get(Get(json.RootElement, "items")[0], "isStale").GetBoolean().Should().BeTrue();
        Get(Get(json.RootElement, "items")[0], "dataAsOf").GetDateTime().Date.Should().Be(snapshotDate);
    }

    [Fact]
    public async Task GetCostByResourceAsync_ShouldUseAnOlderPersistedSnapshotWithinConfiguredLookback()
    {
        const string subscriptionId = "sub";
        var requestedDate = new DateTime(2026, 9, 1);
        var snapshotDate = new DateTime(2026, 8, 18);
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Loose);
        repository.Setup(item => item.LoadByResourceAsync(It.IsAny<DateTime>(), subscriptionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime date, string _, CancellationToken _) => date == snapshotDate
                ? [new CostByResourceRow
                {
                    ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Sql/servers/sql/databases/db",
                    Label = "db", Currency = "BRL", TotalCost = 0.08m, SubscriptionId = subscriptionId
                }]
                : []);
        repository.Setup(item => item.LoadAllocationAsync(snapshotDate, subscriptionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(item => item.LoadPublicationWatermarkAsync(
                subscriptionId, CostExportPublicationModes.Cutover, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostExportPublicationWatermark?)null);
        var sut = CreateFunction(
            repository.Object,
            UserWithReader().Object,
            settings: new Dictionary<string, string?> { ["COST_RESOURCE_SNAPSHOT_LOOKBACK_DAYS"] = "30" });

        var response = await sut.GetCostByResourceAsync(HttpTestHelpers.CreateGetRequest(
            "https://localhost/api/cost/by-resource?date=2026-09-01&subscription=sub"));
        var json = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Get(json.RootElement, "dataAsOf").GetDateTime().Date.Should().Be(snapshotDate);
        Get(json.RootElement, "items").GetArrayLength().Should().Be(1);
        Get(Get(json.RootElement, "items")[0], "isStale").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetCostByResourceAsync_WhenAllSubscriptionsAreRequested_ShouldUseTheLatestSnapshotOfEachSubscription()
    {
        var requestedDate = new DateTime(2026, 9, 1);
        var olderDate = requestedDate.AddDays(-2);
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Loose);
        repository.Setup(item => item.LoadByResourceAllAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime date, CancellationToken _) => date == requestedDate
                ? [new CostByResourceRow { ResourceId = "/subscriptions/new/resource", Label = "new", Currency = "BRL", TotalCost = 1, SubscriptionId = "new" }]
                : date == olderDate
                    ? [new CostByResourceRow { ResourceId = "/subscriptions/old/resource", Label = "old", Currency = "BRL", TotalCost = 2, SubscriptionId = "old" }]
                    : []);
        repository.Setup(item => item.LoadAllocationAllAsync(requestedDate, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        repository.Setup(item => item.LoadPublicationWatermarkAsync(It.IsAny<string>(), CostExportPublicationModes.Cutover, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostExportPublicationWatermark?)null);
        var sut = CreateFunction(repository.Object, UserWithReader().Object);

        var response = await sut.GetCostByResourceAsync(HttpTestHelpers.CreateGetRequest(
            "https://localhost/api/cost/by-resource?date=2026-09-01"));
        var json = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = Get(json.RootElement, "items");
        items.GetArrayLength().Should().Be(2);
        Get(items[0], "subscriptionId").GetString().Should().Be("old");
        Get(items[0], "dataAsOf").GetDateTime().Date.Should().Be(olderDate);
        Get(items[0], "isStale").GetBoolean().Should().BeTrue();
        Get(items[1], "subscriptionId").GetString().Should().Be("new");
        Get(items[1], "dataAsOf").GetDateTime().Date.Should().Be(requestedDate);
    }

    [Fact]
    public async Task GetCostByResourceAsync_ShouldStopScanningAfterTheStaleSpreadWindow()
    {
        // Varrer os 90 dias inteiros custava uma listagem de blobs por dia e travava a grade.
        var requestedDate = new DateTime(2026, 9, 1);
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Loose);
        repository.Setup(item => item.LoadByResourceAllAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime date, CancellationToken _) => date == requestedDate
                ? [new CostByResourceRow { ResourceId = "resource", Label = "resource", Currency = "BRL", TotalCost = 1, SubscriptionId = "sub" }]
                : []);
        repository.Setup(item => item.LoadAllocationAllAsync(requestedDate, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        repository.Setup(item => item.LoadPublicationWatermarkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostExportPublicationWatermark?)null);
        var sut = CreateFunction(repository.Object, UserWithReader().Object, settings: new Dictionary<string, string?>
        {
            ["COST_RESOURCE_SNAPSHOT_LOOKBACK_DAYS"] = "90",
            ["COST_RESOURCE_SNAPSHOT_STALE_SPREAD_DAYS"] = "2"
        });

        var response = await sut.GetCostByResourceAsync(HttpTestHelpers.CreateGetRequest(
            "https://localhost/api/cost/by-resource?date=2026-09-01"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        repository.Verify(
            item => item.LoadByResourceAllAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3),
            "depois do dia mais recente com dados, só a janela de atraso configurada é varrida");
    }

    [Fact]
    public async Task GetCostByResourceAsync_WhenNoDayHasData_ShouldStillHonourTheFullLookback()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Loose);
        repository.Setup(item => item.LoadByResourceAllAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(item => item.LoadAllocationAllAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var sut = CreateFunction(repository.Object, UserWithReader().Object, settings: new Dictionary<string, string?>
        {
            ["COST_RESOURCE_SNAPSHOT_LOOKBACK_DAYS"] = "4",
            ["COST_RESOURCE_SNAPSHOT_STALE_SPREAD_DAYS"] = "1"
        });

        var response = await sut.GetCostByResourceAsync(HttpTestHelpers.CreateGetRequest(
            "https://localhost/api/cost/by-resource?date=2026-09-01"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        repository.Verify(
            item => item.LoadByResourceAllAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Exactly(5),
            "sem nenhum dia com dados a janela de atraso não se aplica");
    }

    [Fact]
    public async Task GetCostByResourceGroupAsync_ShouldSumResourcesOfTheSameResourceGroup()
    {
        var date = new DateTime(2026, 9, 1);
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Loose);
        repository.Setup(item => item.LoadByResourceAllAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime value, CancellationToken _) => value == date
                ? [
                    new CostByResourceRow { ResourceId = "/rg-um/vm-a", Label = "vm-a", ResourceGroup = "rg-um", ServiceName = "Virtual Machines", Currency = "BRL", TotalCost = 10, SubscriptionId = "sub" },
                    new CostByResourceRow { ResourceId = "/rg-um/vm-b", Label = "vm-b", ResourceGroup = "rg-um", ServiceName = "Storage", Currency = "BRL", TotalCost = 20, SubscriptionId = "sub" },
                    new CostByResourceRow { ResourceId = "/rg-dois/vm-c", Label = "vm-c", ResourceGroup = "rg-dois", ServiceName = "Virtual Machines", Currency = "BRL", TotalCost = 5, SubscriptionId = "sub" }
                  ]
                : []);
        repository.Setup(item => item.LoadAllocationAllAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        repository.Setup(item => item.LoadPublicationWatermarkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostExportPublicationWatermark?)null);
        var sut = CreateFunction(repository.Object, UserWithReader().Object);

        var response = await sut.GetCostByResourceGroupAsync(HttpTestHelpers.CreateGetRequest(
            "https://localhost/api/cost/by-resource-group?date=2026-09-01"));
        var json = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = Get(json.RootElement, "items");
        items.GetArrayLength().Should().Be(2);
        Get(items[0], "resourceGroup").GetString().Should().Be("rg-um");
        Get(items[0], "totalCost").GetDecimal().Should().Be(30m);
        Get(items[0], "resourceCount").GetInt32().Should().Be(2);
        Get(items[0], "serviceCount").GetInt32().Should().Be(2);
        Get(items[0], "topService").GetString().Should().Be("Storage");
        Get(json.RootElement, "totalCount").GetInt32().Should().Be(2);
        Get(json.RootElement, "page").GetInt32().Should().Be(1);
        Get(json.RootElement, "pageSize").GetInt32().Should().Be(100);
    }

    [Fact]
    public async Task GetCostByResourceAsync_WhenExportIsInShadow_ShouldReportExportAsSource()
    {
        const string subscriptionId = "sub";
        var date = new DateTime(2026, 9, 1);
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        repository.Setup(item => item.LoadByResourceAllAsync(date, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CostByResourceRow { ResourceId = "resource", Label = "resource", Currency = "BRL", TotalCost = 1, SubscriptionId = subscriptionId }]);
        repository.Setup(item => item.LoadByResourceAllAsync(It.Is<DateTime>(value => value != date), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(item => item.LoadAllocationAllAsync(date, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        repository.Setup(item => item.LoadPublicationWatermarkAsync(subscriptionId, CostExportPublicationModes.Cutover, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostExportPublicationWatermark?)null);
        repository.Setup(item => item.LoadPublicationWatermarkAsync(subscriptionId, CostExportPublicationModes.Shadow, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportPublicationWatermark { SubscriptionId = subscriptionId, Source = CostDataSources.CostManagementExport, MaxUsageDate = date });
        var sut = CreateFunction(repository.Object, UserWithReader().Object, settings: new Dictionary<string, string?>
        {
            ["EnableCostExportImport"] = "true",
            ["COST_EXPORT_IMPORT_ALL_SUBSCRIPTIONS"] = "true"
        });

        var response = await sut.GetCostByResourceAsync(HttpTestHelpers.CreateGetRequest(
            "https://localhost/api/cost/by-resource?date=2026-09-01"));
        var json = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Get(json.RootElement, "source").GetString().Should().Be(CostDataSources.CostManagementExport);
    }

    [Fact]
    public async Task GetCostByServiceAsync_WhenOnlyPartOfSubscriptionsHasExportMetadata_ShouldReturnMixedWithoutPartialAllocation()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        var date = new DateTime(2026, 8, 18);
        repository.Setup(item => item.LoadByServiceAllAsync(date, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CostByServiceRow { Label = "Storage", Currency = "USD", TotalCost = 10, SubscriptionId = "sub-export" },
                new CostByServiceRow { Label = "Compute", Currency = "USD", TotalCost = 20, SubscriptionId = "sub-legacy" }
            ]);
        repository.Setup(item => item.LoadAllocationAllAsync(date, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CostAllocationSummary
                {
                    SubscriptionId = "sub-export",
                    UsageDate = date,
                    Currency = "USD",
                    TotalCost = 10,
                    AllocatedResourceCost = 8,
                    UnallocatedCost = 2
                }
            ]);
        repository.Setup(item => item.LoadPublicationWatermarkAsync(
                "sub-export", CostExportPublicationModes.Cutover, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportPublicationWatermark
            {
                SubscriptionId = "sub-export",
                Source = CostDataSources.CostManagementExport,
                MaxUsageDate = date
            });
        repository.Setup(item => item.LoadPublicationWatermarkAsync(
                "sub-legacy", CostExportPublicationModes.Cutover, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostExportPublicationWatermark?)null);
        var sut = CreateFunction(repository.Object, UserWithReader().Object);

        var response = await sut.GetCostByServiceAsync(HttpTestHelpers.CreateGetRequest(
            "https://localhost/api/cost/by-service?date=2026-08-18"));
        var json = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));

        Get(json.RootElement, "source").GetString().Should().Be("Mixed");
        Get(json.RootElement, "allocation").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetCostTrendByServiceAsync_ShouldNeverSumDifferentCurrencies()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        repository.Setup(item => item.LoadByServiceAllAsync(
                new DateTime(2026, 8, 18), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CostByServiceRow { Label = "Storage", Currency = "USD", TotalCost = 10 },
                new CostByServiceRow { Label = "Storage", Currency = "BRL", TotalCost = 20 }
            ]);
        var sut = CreateFunction(repository.Object, UserWithReader().Object);

        var response = await sut.GetCostTrendByServiceAsync(HttpTestHelpers.CreateGetRequest(
            "https://localhost/api/cost/trend-by-service?from=2026-08-18&to=2026-08-18"));
        var json = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));

        var items = Get(json.RootElement, "items");
        items.GetArrayLength().Should().Be(2);
        items.EnumerateArray().Select(item => Get(item, "currency").GetString())
            .Should().BeEquivalentTo("USD", "BRL");
    }

    [Fact]
    public async Task GetCostTrendByServiceAsync_ShouldPreferVersionedExportThenUsePersistedLegacyAndKeepGaps()
    {
        const string subscriptionId = "00000000-0000-0000-0000-00000000a006";
        var exportDate = new DateTime(2026, 8, 18);
        var legacyDate = exportDate.AddDays(1);
        var missingDate = exportDate.AddDays(2);
        var snapshot = new CostExportDailySnapshot
        {
            SubscriptionId = subscriptionId,
            PublicationMode = CostExportPublicationModes.Shadow,
            Date = exportDate,
            ExportName = "daily-export",
            RunId = "run-1",
            SubmittedTime = new DateTimeOffset(2026, 8, 19, 1, 0, 0, TimeSpan.Zero),
            ImportedAt = new DateTimeOffset(2026, 8, 19, 2, 0, 0, TimeSpan.Zero),
            ByService =
            [
                new CostByServiceRow
                {
                    Label = "Storage",
                    Currency = "BRL",
                    TotalCost = 10,
                    SubscriptionId = subscriptionId
                }
            ]
        };
        var versionStore = new Mock<ICostExportVersionStore>(MockBehavior.Strict);
        versionStore.Setup(item => item.LoadIndexAsync(
                subscriptionId, CostExportPublicationModes.Shadow, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostExportRevisionIndex
            {
                SubscriptionId = subscriptionId,
                PublicationMode = CostExportPublicationModes.Shadow,
                Days = new SortedDictionary<string, CostExportDayReference>(StringComparer.Ordinal)
                {
                    [exportDate.ToString("yyyy-MM-dd")] = new()
                    {
                        Revision = snapshot.Revision,
                        RunId = snapshot.RunId,
                        SubmittedTime = snapshot.SubmittedTime,
                        ImportedAt = snapshot.ImportedAt
                    }
                }
            });
        versionStore.Setup(item => item.LoadIndexAsync(
                subscriptionId, CostExportPublicationModes.Cutover, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostExportRevisionIndex?)null);
        versionStore.Setup(item => item.LoadSnapshotAsync(
                subscriptionId,
                CostExportPublicationModes.Shadow,
                snapshot.Revision,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);

        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        repository.Setup(item => item.LoadByServiceAsync(
                legacyDate, subscriptionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CostByServiceRow
                {
                    Label = "Compute",
                    Currency = "BRL",
                    TotalCost = 20,
                    SubscriptionId = subscriptionId
                }
            ]);
        repository.Setup(item => item.LoadByServiceAsync(
                missingDate, subscriptionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var settings = new Dictionary<string, string?>
        {
            [CostCollectionRouting.EnableExportImportSetting] = "true",
            [CostCollectionRouting.ImportAllExportSubscriptionsSetting] = "true"
        };
        var sut = CreateFunction(
            repository.Object,
            UserWithReader().Object,
            versionStore.Object,
            settings);

        var response = await sut.GetCostTrendByServiceAsync(HttpTestHelpers.CreateGetRequest(
            $"https://localhost/api/cost/trend-by-service?from={exportDate:yyyy-MM-dd}&to={missingDate:yyyy-MM-dd}&subscription={subscriptionId}"));
        var json = JsonDocument.Parse(await HttpTestHelpers.ReadBodyAsStringAsync(response));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Get(json.RootElement, "source").GetString().Should().Be("Mixed");
        Get(json.RootElement, "dataAsOf").GetDateTime().Date.Should().Be(legacyDate);
        Get(json.RootElement, "availableDates").EnumerateArray().Select(item => item.GetString())
            .Should().Equal(exportDate.ToString("yyyy-MM-dd"), legacyDate.ToString("yyyy-MM-dd"));
        Get(json.RootElement, "missingDates").EnumerateArray().Select(item => item.GetString())
            .Should().Equal(missingDate.ToString("yyyy-MM-dd"));
        var sourceByDate = Get(json.RootElement, "sourceByDate");
        Get(sourceByDate, exportDate.ToString("yyyy-MM-dd")).GetString()
            .Should().Be(CostDataSources.CostManagementExport);
        Get(sourceByDate, legacyDate.ToString("yyyy-MM-dd")).GetString()
            .Should().Be(CostDataSources.CostManagementQueryApi);
        Get(json.RootElement, "items").GetArrayLength().Should().Be(2);
        repository.Verify(item => item.LoadByServiceAsync(
            exportDate, subscriptionId, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HealthCheckAsync_ShouldUseGenericRouteAndAuthorization()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        repository.Setup(item => item.CanAccessStorageAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        repository.Setup(item => item.ExistsByServiceDataAsync(
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var sut = CreateFunction(repository.Object, UserWithReader().Object);

        var response = await sut.HealthCheckAsync(
            HttpTestHelpers.CreateGetRequest("https://localhost/api/cost/health"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static CostApiFunction CreateFunction(
        ICostStorageRepository repository,
        ICurrentUserService currentUserService,
        ICostExportVersionStore? costExportVersions = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        var configurationValues = new Dictionary<string, string?>
        {
            ["COST_DATA_DELAY_DAYS"] = "2"
        };
        if (settings is not null)
        {
            foreach (var setting in settings)
            {
                configurationValues[setting.Key] = setting.Value;
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();
        var rollupRepository = new NoOpDatabaseCostRollupRepository();
        var coverageRepository = new NoOpCostAllocationCoverageRepository();
        return new CostApiFunction(
            repository,
            costExportVersions ?? new Mock<ICostExportVersionStore>().Object,
            rollupRepository,
            new DatabaseCostRollupService(
                repository,
                rollupRepository,
                configuration,
                new NullLogger<DatabaseCostRollupService>()),
            coverageRepository,
            new CostAllocationCoverageService(
                repository,
                coverageRepository,
                configuration,
                new NullLogger<CostAllocationCoverageService>()),
            configuration,
            currentUserService,
            new AuthorizationService(),
            new NullLogger<CostApiFunction>());
    }

    private static Mock<ICurrentUserService> UserWithReader() => User(new CurrentUserContext
    {
        IsAuthenticated = true,
        Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FinOpsRoles.Reader }
    });

    private static Mock<ICurrentUserService> User(CurrentUserContext context)
    {
        var mock = new Mock<ICurrentUserService>(MockBehavior.Strict);
        mock.Setup(item => item.GetCurrentUserAsync(
                It.IsAny<Microsoft.Azure.Functions.Worker.Http.HttpRequestData>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);
        return mock;
    }

    private static JsonElement Get(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new KeyNotFoundException(name);
    }

}
