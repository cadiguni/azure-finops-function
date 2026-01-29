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
/// Testes para CostManagementService com mocks do HttpClient
/// </summary>
public class CostManagementServiceTests
{
    private readonly Mock<ILogger<CostManagementService>> _loggerMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly CostManagementService _service;

    public CostManagementServiceTests()
    {
        _loggerMock = new Mock<ILogger<CostManagementService>>();
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpMessageHandlerMock.Object)
        {
            BaseAddress = new Uri("https://management.azure.com/")
        };
        _service = new CostManagementService(_httpClient, _loggerMock.Object);
    }

    [Fact]
    public async Task Should_Retrieve_Cost_Data_Successfully()
    {
        // Arrange
        var expectedCostData = new List<CostRecord>
        {
            new() 
            { 
                ResourceId = "/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1",
                ResourceName = "vm1",
                ResourceType = "Microsoft.Compute/virtualMachines",
                Cost = 150.75m,
                Currency = "BRL",
                UsageDate = DateTime.UtcNow.Date.AddDays(-1),
                MeterId = "meter-123"
            }
        };

        var responseContent = new
        {
            properties = new
            {
                rows = new object[][]
                {
                    new object[] 
                    { 
                        150.75,
                        "BRL", 
                        "/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1",
                        "vm1",
                        "Microsoft.Compute/virtualMachines",
                        DateTime.UtcNow.Date.AddDays(-1).ToString("yyyy-MM-dd"),
                        "meter-123"
                    }
                }
            }
        };

        var jsonResponse = JsonSerializer.Serialize(responseContent);
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse)
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetCostDataAsync("/subscriptions/test-subscription", 
                                                   DateTime.UtcNow.AddDays(-30), 
                                                   DateTime.UtcNow);

        // Assert
        result.Should().HaveCount(1);
        var costRecord = result.First();
        costRecord.Cost.Should().Be(150.75m);
        costRecord.ResourceName.Should().Be("vm1");
        costRecord.Currency.Should().Be("BRL");
    }

    [Fact]
    public async Task Should_Handle_Http_Error_Gracefully()
    {
        // Arrange
        var httpResponse = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Unauthorized")
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
            .Invoking(() => _service.GetCostDataAsync("/subscriptions/test-subscription", 
                                                    DateTime.UtcNow.AddDays(-30), 
                                                    DateTime.UtcNow))
            .Should()
            .ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Should_Filter_Cost_Data_By_Date_Range()
    {
        // Arrange
        var startDate = DateTime.UtcNow.AddDays(-7);
        var endDate = DateTime.UtcNow;

        var responseContent = new
        {
            properties = new
            {
                rows = new object[][]
                {
                    new object[] 
                    { 
                        100.0,
                        "BRL", 
                        "/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1",
                        "vm1",
                        "Microsoft.Compute/virtualMachines",
                        startDate.ToString("yyyy-MM-dd"),
                        "meter-123"
                    },
                    new object[] 
                    { 
                        50.0,
                        "BRL", 
                        "/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm2",
                        "vm2",
                        "Microsoft.Compute/virtualMachines",
                        startDate.AddDays(1).ToString("yyyy-MM-dd"),
                        "meter-124"
                    }
                }
            }
        };

        var jsonResponse = JsonSerializer.Serialize(responseContent);
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse)
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetCostDataAsync("/subscriptions/test-subscription", startDate, endDate);

        // Assert
        result.Should().HaveCount(2);
        result.All(r => r.UsageDate >= startDate.Date && r.UsageDate <= endDate.Date).Should().BeTrue();
    }

    [Theory]
    [InlineData("Microsoft.Compute/virtualMachines")]
    [InlineData("Microsoft.Storage/storageAccounts")]
    [InlineData("Microsoft.Web/sites")]
    public async Task Should_Handle_Different_Resource_Types(string resourceType)
    {
        // Arrange
        var responseContent = new
        {
            properties = new
            {
                rows = new object[][]
                {
                    new object[] 
                    { 
                        75.5,
                        "BRL", 
                        $"/subscriptions/sub1/resourceGroups/rg1/providers/{resourceType}/resource1",
                        "resource1",
                        resourceType,
                        DateTime.UtcNow.Date.ToString("yyyy-MM-dd"),
                        "meter-test"
                    }
                }
            }
        };

        var jsonResponse = JsonSerializer.Serialize(responseContent);
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse)
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetCostDataAsync("/subscriptions/test-subscription", 
                                                   DateTime.UtcNow.AddDays(-1), 
                                                   DateTime.UtcNow);

        // Assert
        result.Should().HaveCount(1);
        result.First().ResourceType.Should().Be(resourceType);
    }

    [Fact]
    public async Task Should_Return_Empty_List_When_No_Data_Available()
    {
        // Arrange
        var responseContent = new
        {
            properties = new
            {
                rows = Array.Empty<object[]>()
            }
        };

        var jsonResponse = JsonSerializer.Serialize(responseContent);
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse)
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(httpResponse);

        // Act
        var result = await _service.GetCostDataAsync("/subscriptions/test-subscription", 
                                                   DateTime.UtcNow.AddDays(-30), 
                                                   DateTime.UtcNow);

        // Assert
        result.Should().BeEmpty();
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