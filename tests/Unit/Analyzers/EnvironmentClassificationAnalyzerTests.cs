using FluentAssertions;
using Personal.FinOpsApi.Domain.Analyzers;
using Personal.FinOpsApi.Domain.Configuration;
using Personal.FinOpsApi.UnitTests.Fixtures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.UnitTests.Analyzers;

/// <summary>
/// Testes para EnvironmentClassificationAnalyzer
/// </summary>
public class EnvironmentClassificationAnalyzerTests
{
    private readonly Mock<ILogger<EnvironmentClassificationAnalyzer>> _loggerMock;
    private readonly Mock<IOptions<EnvironmentClassificationOptions>> _optionsMock;
    private readonly EnvironmentClassificationAnalyzer _analyzer;

    public EnvironmentClassificationAnalyzerTests()
    {
        _loggerMock = new Mock<ILogger<EnvironmentClassificationAnalyzer>>();
        _optionsMock = new Mock<IOptions<EnvironmentClassificationOptions>>();
        
        // Setup default configuration
        var config = new EnvironmentClassificationOptions
        {
            ProductionEnvironments = new[] { "prod", "production" },
            DevelopmentEnvironments = new[] { "dev", "development", "test", "staging" },
            ProductionBehavior = new BehaviorOptions 
            { 
                EnableOptimizations = true, 
                AlertThreshold = 0.05, 
                RequireApproval = true 
            },
            DevelopmentBehavior = new BehaviorOptions 
            { 
                EnableOptimizations = true, 
                AlertThreshold = 0.10, 
                RequireApproval = false 
            }
        };
        
        _optionsMock.Setup(x => x.Value).Returns(config);
        _analyzer = new EnvironmentClassificationAnalyzer(_loggerMock.Object, _optionsMock.Object);
    }

    [Theory]
    [InlineData("prod", "Production")]
    [InlineData("production", "Production")]
    [InlineData("dev", "Development")]
    [InlineData("development", "Development")]
    [InlineData("test", "Development")]
    [InlineData("staging", "Development")]
    public void Should_Classify_Environment_Correctly(string environmentTag, string expectedClassification)
    {
        // Arrange
        var resourceUsage = FakeDataFactory.CreateResourceWithEnvironmentTag(environmentTag);

        // Act
        var findings = _analyzer.AnalyzeAsync(resourceUsage).Result;

        // Assert
        findings.Should().HaveCount(1);
        var finding = findings.First();
        finding.Metrics["EnvironmentClassification"].Should().Be(expectedClassification);
    }

    [Fact]
    public void Should_Apply_Production_Behavior_For_Prod_Environment()
    {
        // Arrange
        var prodResourceUsage = FakeDataFactory.CreateResourceWithEnvironmentTag("prod");

        // Act
        var findings = _analyzer.AnalyzeAsync(prodResourceUsage).Result;

        // Assert
        var finding = findings.First();
        finding.Metrics["RequireApproval"].Should().Be(true);
        finding.Metrics["AlertThreshold"].Should().Be(0.05);
        finding.Severity.Should().Be("High"); // Produção deve ter severidade alta
    }

    [Fact]
    public void Should_Apply_Development_Behavior_For_Dev_Environment()
    {
        // Arrange
        var devResourceUsage = FakeDataFactory.CreateResourceWithEnvironmentTag("dev");

        // Act
        var findings = _analyzer.AnalyzeAsync(devResourceUsage).Result;

        // Assert
        var finding = findings.First();
        finding.Metrics["RequireApproval"].Should().Be(false);
        finding.Metrics["AlertThreshold"].Should().Be(0.10);
        finding.Severity.Should().Be("Medium"); // Desenvolvimento pode ter severidade menor
    }

    [Fact]
    public void Should_Handle_Unknown_Environment_Classification()
    {
        // Arrange
        var unknownEnvResourceUsage = FakeDataFactory.CreateResourceWithEnvironmentTag("unknown-env");

        // Act
        var findings = _analyzer.AnalyzeAsync(unknownEnvResourceUsage).Result;

        // Assert
        var finding = findings.First();
        finding.Metrics["EnvironmentClassification"].Should().Be("Unknown");
        finding.Severity.Should().Be("Low");
        finding.Recommendation.Should().Contain("classificar corretamente o ambiente");
    }

    [Fact]
    public void Should_Flag_Resources_Without_Environment_Tag()
    {
        // Arrange
        var resourceWithoutEnvTag = FakeDataFactory.CreateResourcesWithMissingTags();
        // Garantir que a tag Environment não existe
        resourceWithoutEnvTag[0].Tags.Remove("Environment");

        // Act
        var findings = _analyzer.AnalyzeAsync(resourceWithoutEnvTag).Result;

        // Assert
        var finding = findings.First();
        finding.Title.Should().Contain("sem classificação de ambiente");
        finding.Recommendation.Should().Contain("adicionar tag Environment");
        finding.Severity.Should().Be("Medium");
    }

    [Fact]
    public void Should_Include_Environment_Specific_Recommendations()
    {
        // Arrange
        var prodResourceUsage = FakeDataFactory.CreateResourceWithEnvironmentTag("prod");

        // Act
        var findings = _analyzer.AnalyzeAsync(prodResourceUsage).Result;

        // Assert
        var finding = findings.First();
        finding.Recommendation.Should().Contain("ambiente de produção");
        finding.Details.Should().Contain("aprovação obrigatória");
    }

    [Fact]
    public void Should_Calculate_Environment_Specific_Cost_Impact()
    {
        // Arrange
        var prodResourceUsage = FakeDataFactory.CreateResourceWithEnvironmentTag("prod");
        var devResourceUsage = FakeDataFactory.CreateResourceWithEnvironmentTag("dev");

        // Act
        var prodFindings = _analyzer.AnalyzeAsync(prodResourceUsage).Result;
        var devFindings = _analyzer.AnalyzeAsync(devResourceUsage).Result;

        // Assert
        var prodFinding = prodFindings.First();
        var devFinding = devFindings.First();

        // Produção deve ter impacto maior devido ao threshold menor (5% vs 10%)
        prodFinding.PotentialMonthlySaving.Should().BeGreaterThan(devFinding.PotentialMonthlySaving);
    }

    [Theory]
    [InlineData("prod", true, "aprovação")]
    [InlineData("dev", false, "automaticamente")]
    public void Should_Include_Approval_Requirements_In_Recommendations(
        string environment, 
        bool requiresApproval, 
        string expectedText)
    {
        // Arrange
        var resourceUsage = FakeDataFactory.CreateResourceWithEnvironmentTag(environment);

        // Act
        var findings = _analyzer.AnalyzeAsync(resourceUsage).Result;

        // Assert
        var finding = findings.First();
        finding.Recommendation.Should().Contain(expectedText);
        finding.Metrics["RequireApproval"].Should().Be(requiresApproval);
    }

    [Fact]
    public void Should_Handle_Multiple_Resources_With_Different_Environments()
    {
        // Arrange
        var mixedEnvironmentResources = FakeDataFactory.CreateMixedEnvironmentScenarios();

        // Act
        var findings = _analyzer.AnalyzeAsync(mixedEnvironmentResources).Result;

        // Assert
        findings.Should().HaveCount(3); // Prod, Dev, Unknown

        var prodFinding = findings.FirstOrDefault(f => f.Metrics.ContainsValue("Production"));
        var devFinding = findings.FirstOrDefault(f => f.Metrics.ContainsValue("Development"));
        var unknownFinding = findings.FirstOrDefault(f => f.Metrics.ContainsValue("Unknown"));

        prodFinding.Should().NotBeNull();
        devFinding.Should().NotBeNull();
        unknownFinding.Should().NotBeNull();

        // Verificar severidades diferenciadas
        prodFinding.Severity.Should().Be("High");
        devFinding.Severity.Should().Be("Medium");
        unknownFinding.Severity.Should().Be("Low");
    }
}
