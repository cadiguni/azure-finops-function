using FluentAssertions;
using Personal.FinOpsApi.Domain.Analyzers;
using Personal.FinOpsApi.UnitTests.Fixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.UnitTests.Analyzers;

/// <summary>
/// Testes para AppServiceAnalyzer
/// </summary>
public class AppServiceAnalyzerTests
{
    private readonly Mock<ILogger<AppServiceAnalyzer>> _loggerMock;
    private readonly AppServiceAnalyzer _analyzer;

    public AppServiceAnalyzerTests()
    {
        _loggerMock = new Mock<ILogger<AppServiceAnalyzer>>();
        _analyzer = new AppServiceAnalyzer(_loggerMock.Object);
    }

    [Fact]
    public void Should_Identify_Low_Traffic_App_Service()
    {
        // Arrange
        var lowTrafficAppService = FakeDataFactory.CreateLowTrafficAppServiceUsage();

        // Act
        var findings = _analyzer.AnalyzeAsync(lowTrafficAppService).Result;

        // Assert
        findings.Should().HaveCount(1);

        var finding = findings.First();
        finding.Category.Should().Be("App Service");
        finding.Severity.Should().Be("Medium");
        finding.ResourceName.Should().Be("app-low-traffic-01");
        finding.Title.Should().Contain("baixo tráfego");
    }

    [Fact]
    public void Should_Calculate_Correct_Saving_For_Plan_Downgrade()
    {
        // Arrange
        var lowTrafficAppService = FakeDataFactory.CreateLowTrafficAppServiceUsage();
        var expectedSaving = 20 * 30 * 0.6; // 20 BRL/dia, economia de 60% descendo de Standard para Basic

        // Act
        var findings = _analyzer.AnalyzeAsync(lowTrafficAppService).Result;

        // Assert
        var finding = findings.First();
        finding.PotentialMonthlySaving.Should().BeApproximately(expectedSaving, 50);
    }

    [Theory]
    [InlineData(50, 1.2, 10, true)]   // Baixo tráfego, baixo CPU, poucos requests
    [InlineData(500, 2.5, 100, false)] // Tráfego normal
    [InlineData(1000, 15.0, 500, false)] // Alto tráfego
    public void Should_Apply_Traffic_Thresholds_Correctly(
        int dailyRequests, 
        double avgCpuPercent, 
        int avgResponseTimeMs,
        bool shouldFlag)
    {
        // Arrange
        var appServiceUsage = FakeDataFactory.CreateLowTrafficAppServiceUsage();
        appServiceUsage[0].RequestCount = dailyRequests;
        appServiceUsage[0].CpuPercentage = avgCpuPercent;
        appServiceUsage[0].AverageResponseTime = avgResponseTimeMs;

        // Act
        var findings = _analyzer.AnalyzeAsync(appServiceUsage).Result;

        // Assert
        if (shouldFlag)
        {
            findings.Should().HaveCount(1);
        }
        else
        {
            findings.Should().BeEmpty();
        }
    }

    [Fact]
    public void Should_Not_Flag_High_Traffic_App_Service()
    {
        // Arrange
        var highTrafficAppService = FakeDataFactory.CreateHighTrafficAppServiceUsage();

        // Act
        var findings = _analyzer.AnalyzeAsync(highTrafficAppService).Result;

        // Assert
        findings.Should().BeEmpty("App Service com alto tráfego não deve ser sinalizado");
    }

    [Fact]
    public void Should_Include_Performance_Metrics_In_Analysis()
    {
        // Arrange
        var lowTrafficAppService = FakeDataFactory.CreateLowTrafficAppServiceUsage();

        // Act
        var findings = _analyzer.AnalyzeAsync(lowTrafficAppService).Result;

        // Assert
        var finding = findings.First();
        finding.Metrics.Should().ContainKey("RequestCount");
        finding.Metrics.Should().ContainKey("CpuPercentage");
        finding.Metrics.Should().ContainKey("AverageResponseTime");
        finding.Metrics["RequestCount"].Should().Be(50);
        finding.Metrics["CpuPercentage"].Should().Be(1.2);
    }

    [Fact]
    public void Should_Recommend_Appropriate_App_Service_Plan_Downgrade()
    {
        // Arrange
        var lowTrafficAppService = FakeDataFactory.CreateLowTrafficAppServiceUsage();

        // Act
        var findings = _analyzer.AnalyzeAsync(lowTrafficAppService).Result;

        // Assert
        var finding = findings.First();
        finding.Recommendation.Should().Contain("Basic");
        finding.Recommendation.Should().Contain("reduzir o plano");
        finding.Impact.Should().Be("Médio");
    }

    [Fact]
    public void Should_Consider_App_Service_Plan_Pricing_Tier()
    {
        // Arrange
        var appServiceUsage = FakeDataFactory.CreateLowTrafficAppServiceUsage();
        appServiceUsage[0].PricingTier = "Premium_V2"; // Tier mais caro

        // Act
        var findings = _analyzer.AnalyzeAsync(appServiceUsage).Result;

        // Assert
        var finding = findings.First();
        finding.PotentialMonthlySaving.Should().BeGreaterThan(300); // Economia maior para tier Premium
        finding.Recommendation.Should().Contain("Standard");
    }
}
