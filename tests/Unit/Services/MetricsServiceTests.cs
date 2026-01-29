using FluentAssertions;
using Gvdasa.FinOpsApi.Infra.Services;
using Gvdasa.FinOpsApi.Modelos;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Gvdasa.FinOpsApi.UnitTests.Services;

/// <summary>
/// Testes para MetricsService com mocks do HttpClient
/// </summary>
public class MetricsServiceTests
{
    private readonly Mock<ILogger<MetricsService>> _loggerMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly MetricsService _service;

    public MetricsServiceTests()
    {
        _loggerMock = new Mock<ILogger<MetricsService>>();
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpMessageHandlerMock.Object)
        {
            BaseAddress = new Uri("https://management.azure.com/")
        };
        _service = new MetricsService(_httpClient, _loggerMock.Object);
    }

    [Fact]
    public async Task Should_Retrieve_Vm_Metrics_Successfully()
    {
        // Arrange
        var resourceId = "/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1";
        var expectedMetrics = CreateVmMetricsResponse();

        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(expectedMetrics))
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetVmMetricsAsync(resourceId, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        // Assert
        result.Should().HaveCount(1);
        var vmUsage = result.First();
        vmUsage.ResourceId.Should().Be(resourceId);
        vmUsage.CpuPercentage.Should().Be(2.5);
        vmUsage.MemoryPercentage.Should().Be(15.3);
        vmUsage.NetworkIn.Should().Be(1024);
        vmUsage.NetworkOut.Should().Be(2048);
    }

    [Fact]
    public async Task Should_Retrieve_Disk_Metrics_Successfully()
    {
        // Arrange
        var resourceId = "/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.Compute/disks/disk1";
        var expectedMetrics = CreateDiskMetricsResponse();

        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(expectedMetrics))
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetDiskMetricsAsync(resourceId, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        // Assert
        result.Should().HaveCount(1);
        var diskUsage = result.First();
        diskUsage.ResourceId.Should().Be(resourceId);
        diskUsage.IsAttached.Should().BeFalse();
        diskUsage.DiskSizeGB.Should().Be(128);
        diskUsage.DiskType.Should().Be("Premium_LRS");
    }

    [Fact]
    public async Task Should_Retrieve_App_Service_Metrics_Successfully()
    {
        // Arrange
        var resourceId = "/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.Web/sites/webapp1";
        var expectedMetrics = CreateAppServiceMetricsResponse();

        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(expectedMetrics))
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetAppServiceMetricsAsync(resourceId, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        // Assert
        result.Should().HaveCount(1);
        var appServiceUsage = result.First();
        appServiceUsage.ResourceId.Should().Be(resourceId);
        appServiceUsage.RequestCount.Should().Be(150);
        appServiceUsage.CpuPercentage.Should().Be(5.7);
        appServiceUsage.AverageResponseTime.Should().Be(250);
        appServiceUsage.PricingTier.Should().Be("Standard");
    }

    [Fact]
    public async Task Should_Handle_Http_Error_In_Metrics_Request()
    {
        // Arrange
        var httpResponse = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("Forbidden")
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act & Assert
        await FluentActions
            .Invoking(() => _service.GetVmMetricsAsync("/invalid/resource", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow))
            .Should()
            .ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Should_Return_Empty_List_When_No_Metrics_Available()
    {
        // Arrange
        var emptyResponse = new
        {
            value = Array.Empty<object>()
        };

        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(emptyResponse))
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetVmMetricsAsync("/valid/resource", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        // Assert
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    public async Task Should_Handle_Different_Time_Ranges(int daysBack)
    {
        // Arrange
        var startDate = DateTime.UtcNow.AddDays(-daysBack);
        var endDate = DateTime.UtcNow;
        var expectedMetrics = CreateVmMetricsResponse();

        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(expectedMetrics))
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetVmMetricsAsync("/test/resource", startDate, endDate);

        // Assert
        result.Should().NotBeEmpty();
        result.All(r => r.Timestamp >= startDate && r.Timestamp <= endDate).Should().BeTrue();
    }

    private static object CreateVmMetricsResponse()
    {
        return new
        {
            value = new[]
            {
                new
                {
                    id = "/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1",
                    name = new { value = "Percentage CPU", localizedValue = "Percentage CPU" },
                    timeseries = new[]
                    {
                        new
                        {
                            data = new[]
                            {
                                new
                                {
                                    timeStamp = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                    average = 2.5,
                                    metricName = "CpuPercentage"
                                }
                            }
                        }
                    }
                },
                new
                {
                    id = "/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1",
                    name = new { value = "Available Memory Bytes", localizedValue = "Available Memory Bytes" },
                    timeseries = new[]
                    {
                        new
                        {
                            data = new[]
                            {
                                new
                                {
                                    timeStamp = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                    average = 15.3,
                                    metricName = "MemoryPercentage"
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private static object CreateDiskMetricsResponse()
    {
        return new
        {
            value = new[]
            {
                new
                {
                    id = "/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.Compute/disks/disk1",
                    properties = new
                    {
                        diskSizeGB = 128,
                        diskState = "Unattached",
                        sku = new { name = "Premium_LRS" }
                    }
                }
            }
        };
    }

    private static object CreateAppServiceMetricsResponse()
    {
        return new
        {
            value = new[]
            {
                new
                {
                    id = "/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.Web/sites/webapp1",
                    name = new { value = "Requests", localizedValue = "Requests" },
                    timeseries = new[]
                    {
                        new
                        {
                            data = new[]
                            {
                                new
                                {
                                    timeStamp = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                    total = 150,
                                    metricName = "RequestCount"
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient?.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}