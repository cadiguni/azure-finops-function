using System.Net;
using System.Text.Json;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Functions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.UnitTests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Functions;

public class GrafanaApiFunctionTests
{
    [Fact]
    public async Task GetCostByServiceAsync_ShouldAggregateRowsForAllSubscriptions()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        repository.Setup(r => r.LoadByServiceAllAsync(
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CostByServiceRow>
            {
                new() { Label = "Azure App Service", Currency = "BRL", TotalCost = 100m, Count = 0 },
                new() { Label = "Azure App Service", Currency = "BRL", TotalCost = 50m, Count = 2 },
                new() { Label = "Azure Storage", Currency = "BRL", TotalCost = 10m, Count = 1 }
            });

        var sut = new GrafanaApiFunction(
            grafanaService: null!,
            repository.Object,
            new NullLogger<GrafanaApiFunction>());

        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/GrafanaCostByService?date=2026-02-22&subscription=all");
        var response = await sut.GetCostByServiceAsync(req);
        var body = await HttpTestHelpers.ReadBodyAsStringAsync(response);
        var rows = JsonSerializer.Deserialize<List<CostByServiceRow>>(body)!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        rows.Should().HaveCount(2);
        rows[0].Label.Should().Be("Azure App Service");
        rows[0].TotalCost.Should().Be(150m);
        rows[0].Count.Should().Be(3); // Math.Max(1,0) + 2
    }

    [Fact]
    public async Task GetCostTrendByServiceAsync_ShouldReturnDailySeriesAndSwapDatesWhenNeeded()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);

        repository.Setup(r => r.LoadByServiceAllAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime date, CancellationToken _) =>
            {
                if (date.Date == new DateTime(2026, 2, 1))
                {
                    return new List<CostByServiceRow>
                    {
                        new() { Label = "Azure App Service", Currency = "BRL", TotalCost = 10m, Count = 1 }
                    };
                }

                if (date.Date == new DateTime(2026, 2, 2))
                {
                    return new List<CostByServiceRow>
                    {
                        new() { Label = "Azure App Service", Currency = "BRL", TotalCost = 20m, Count = 1 },
                        new() { Label = "Azure Storage", Currency = "BRL", TotalCost = 5m, Count = 1 }
                    };
                }

                return new List<CostByServiceRow>();
            });

        var sut = new GrafanaApiFunction(
            grafanaService: null!,
            repository.Object,
            new NullLogger<GrafanaApiFunction>());

        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/GrafanaCostTrendByService?from=2026-02-03&to=2026-02-01&subscription=all&service=Azure%20App%20Service");
        var response = await sut.GetCostTrendByServiceAsync(req);
        var body = await HttpTestHelpers.ReadBodyAsStringAsync(response);
        using var doc = JsonDocument.Parse(body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var arr = doc.RootElement.EnumerateArray().ToList();
        arr.Should().HaveCount(3);
        var totals = arr
            .Select(x => GetPropertyIgnoreCase(x, "totalCost").GetDecimal())
            .ToList();
        totals.Should().Contain(10m);
        totals.Should().Contain(20m);
        totals.Should().Contain(0m);
    }

    [Fact]
    public async Task HealthCheckAsync_WhenStorageOrDataMissing_ShouldReturnServiceUnavailable()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        repository.Setup(r => r.CanAccessStorageAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        repository.Setup(r => r.ExistsByServiceDataAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var sut = new GrafanaApiFunction(
            grafanaService: null!,
            repository.Object,
            new NullLogger<GrafanaApiFunction>());

        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/grafana/health");
        var response = await sut.HealthCheckAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task GetCostByResourceAsync_ShouldAggregateByResource()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);
        repository.Setup(r => r.LoadByResourceAllAsync(
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CostByResourceRow>
            {
                new()
                {
                    Label = "web-a",
                    ResourceId = "/subscriptions/sub-a/resourceGroups/rg/providers/Microsoft.Web/sites/web-a",
                    ServiceName = "Azure App Service",
                    Currency = "BRL",
                    TotalCost = 100m,
                    Count = 1
                },
                new()
                {
                    Label = "web-a",
                    ResourceId = "/subscriptions/sub-a/resourceGroups/rg/providers/Microsoft.Web/sites/web-a",
                    ServiceName = "Azure App Service",
                    Currency = "BRL",
                    TotalCost = 25m,
                    Count = 1
                }
            });

        var sut = new GrafanaApiFunction(
            grafanaService: null!,
            repository.Object,
            new NullLogger<GrafanaApiFunction>());

        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/GrafanaCostByResource?date=2026-02-22&subscription=all&service=Azure%20App%20Service");
        var response = await sut.GetCostByResourceAsync(req);
        var body = await HttpTestHelpers.ReadBodyAsStringAsync(response);
        var rows = JsonSerializer.Deserialize<List<CostByResourceRow>>(body)!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        rows.Should().HaveCount(1);
        rows[0].Label.Should().Be("web-a");
        rows[0].TotalCost.Should().Be(125m);
    }

    [Fact]
    public async Task GetCostTrendByResourceAsync_ShouldFilterByResourceName()
    {
        var repository = new Mock<ICostStorageRepository>(MockBehavior.Strict);

        repository.Setup(r => r.LoadByResourceAllAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime date, CancellationToken _) =>
            {
                if (date.Date == new DateTime(2026, 2, 1))
                {
                    return new List<CostByResourceRow>
                    {
                        new()
                        {
                            Label = "web-a",
                            ResourceId = "/subscriptions/sub-a/resourceGroups/rg/providers/Microsoft.Web/sites/web-a",
                            ServiceName = "Azure App Service",
                            Currency = "BRL",
                            TotalCost = 10m
                        }
                    };
                }

                if (date.Date == new DateTime(2026, 2, 2))
                {
                    return new List<CostByResourceRow>
                    {
                        new()
                        {
                            Label = "web-b",
                            ResourceId = "/subscriptions/sub-a/resourceGroups/rg/providers/Microsoft.Web/sites/web-b",
                            ServiceName = "Azure App Service",
                            Currency = "BRL",
                            TotalCost = 20m
                        }
                    };
                }

                return new List<CostByResourceRow>();
            });

        var sut = new GrafanaApiFunction(
            grafanaService: null!,
            repository.Object,
            new NullLogger<GrafanaApiFunction>());

        var req = HttpTestHelpers.CreateGetRequest("https://localhost/api/GrafanaCostTrendByResource?from=2026-02-01&to=2026-02-02&subscription=all&resource=web-a&service=Azure%20App%20Service");
        var response = await sut.GetCostTrendByResourceAsync(req);
        var body = await HttpTestHelpers.ReadBodyAsStringAsync(response);
        using var doc = JsonDocument.Parse(body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var arr = doc.RootElement.EnumerateArray().ToList();
        arr.Should().HaveCount(2);
        var totals = arr.Select(x => GetPropertyIgnoreCase(x, "totalCost").GetDecimal()).ToList();
        totals.Should().Contain(10m);
        totals.Should().Contain(0m);
    }

    private static JsonElement GetPropertyIgnoreCase(JsonElement element, string propertyName)
    {
        foreach (var p in element.EnumerateObject())
        {
            if (string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }

        throw new KeyNotFoundException(propertyName);
    }
}
