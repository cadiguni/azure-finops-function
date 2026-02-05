using FluentAssertions;
using Personal.FinOpsApi.Domain.Analyzers;
using Personal.FinOpsApi.UnitTests.Fixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.UnitTests.Analyzers;

/// <summary>
/// Testes para GovernanceTagsAnalyzer
/// </summary>
public class GovernanceTagsAnalyzerTests
{
    private readonly Mock<ILogger<GovernanceTagsAnalyzer>> _loggerMock;
    private readonly GovernanceTagsAnalyzer _analyzer;

    public GovernanceTagsAnalyzerTests()
    {
        _loggerMock = new Mock<ILogger<GovernanceTagsAnalyzer>>();
        _analyzer = new GovernanceTagsAnalyzer(_loggerMock.Object);
    }

    [Fact]
    public void Should_Identify_Resources_With_Missing_Required_Tags()
    {
        // Arrange
        var resourcesWithMissingTags = FakeDataFactory.CreateResourcesWithMissingTags();

        // Act
        var findings = _analyzer.AnalyzeAsync(resourcesWithMissingTags).Result;

        // Assert
        findings.Should().HaveCount(1);

        var finding = findings.First();
        finding.Category.Should().Be("Governance");
        finding.Severity.Should().Be("Medium");
        finding.ResourceName.Should().Be("vm-without-tags");
        finding.Title.Should().Contain("tags obrigatórias ausentes");
    }

    [Fact]
    public void Should_Not_Flag_Resources_With_All_Required_Tags()
    {
        // Arrange
        var resourcesWithAllTags = FakeDataFactory.CreateResourcesWithAllRequiredTags();

        // Act
        var findings = _analyzer.AnalyzeAsync(resourcesWithAllTags).Result;

        // Assert
        findings.Should().BeEmpty("Recursos com todas as tags obrigatórias não devem ser sinalizados");
    }

    [Theory]
    [InlineData("Environment", true)]
    [InlineData("CostCenter", true)]
    [InlineData("Owner", true)]
    [InlineData("Project", true)]
    [InlineData("OptionalTag", false)]
    public void Should_Validate_Required_Tags_Correctly(string tagName, bool isRequired)
    {
        // Arrange
        var resourceUsage = FakeDataFactory.CreateResourcesWithMissingTags();
        
        // Simular que só essa tag específica está faltando
        var resource = resourceUsage[0];
        resource.Tags.Clear();
        resource.Tags.Add("Environment", "prod");
        resource.Tags.Add("CostCenter", "IT");
        resource.Tags.Add("Owner", "team@company.com");
        resource.Tags.Add("Project", "FinOps");
        
        // Remover a tag sendo testada se ela é obrigatória
        if (isRequired)
        {
            resource.Tags.Remove(tagName);
        }

        // Act
        var findings = _analyzer.AnalyzeAsync(resourceUsage).Result;

        // Assert
        if (isRequired)
        {
            findings.Should().HaveCount(1);
            var finding = findings.First();
            finding.Details.Should().Contain(tagName);
        }
        else
        {
            findings.Should().BeEmpty();
        }
    }

    [Fact]
    public void Should_List_All_Missing_Tags_In_Finding_Details()
    {
        // Arrange
        var resourceWithNoTags = FakeDataFactory.CreateResourcesWithMissingTags();
        resourceWithNoTags[0].Tags.Clear(); // Remover todas as tags

        // Act
        var findings = _analyzer.AnalyzeAsync(resourceWithNoTags).Result;

        // Assert
        var finding = findings.First();
        finding.Details.Should().Contain("Environment");
        finding.Details.Should().Contain("CostCenter");
        finding.Details.Should().Contain("Owner");
        finding.Details.Should().Contain("Project");
    }

    [Fact]
    public void Should_Include_Tag_Information_In_Metrics()
    {
        // Arrange
        var resourcesWithMissingTags = FakeDataFactory.CreateResourcesWithMissingTags();

        // Act
        var findings = _analyzer.AnalyzeAsync(resourcesWithMissingTags).Result;

        // Assert
        var finding = findings.First();
        finding.Metrics.Should().ContainKey("MissingTagsCount");
        finding.Metrics.Should().ContainKey("TotalRequiredTags");
        finding.Metrics["MissingTagsCount"].Should().BeGreaterThan(0);
        finding.Metrics["TotalRequiredTags"].Should().Be(4); // Environment, CostCenter, Owner, Project
    }

    [Fact]
    public void Should_Provide_Governance_Compliance_Recommendation()
    {
        // Arrange
        var resourcesWithMissingTags = FakeDataFactory.CreateResourcesWithMissingTags();

        // Act
        var findings = _analyzer.AnalyzeAsync(resourcesWithMissingTags).Result;

        // Assert
        var finding = findings.First();
        finding.Recommendation.Should().Contain("aplicar as tags");
        finding.Recommendation.Should().Contain("governança");
        finding.Impact.Should().Be("Médio");
    }

    [Theory]
    [InlineData("prod", true)]
    [InlineData("production", true)]
    [InlineData("dev", true)]
    [InlineData("development", true)]
    [InlineData("test", true)]
    [InlineData("staging", true)]
    [InlineData("invalid-env", false)]
    public void Should_Validate_Environment_Tag_Values(string environmentValue, bool isValid)
    {
        // Arrange
        var resourceUsage = FakeDataFactory.CreateResourcesWithAllRequiredTags();
        resourceUsage[0].Tags["Environment"] = environmentValue;

        // Act
        var findings = _analyzer.AnalyzeAsync(resourceUsage).Result;

        // Assert
        if (!isValid)
        {
            findings.Should().HaveCount(1);
            var finding = findings.First();
            finding.Details.Should().Contain("valor inválido");
        }
        else
        {
            findings.Should().BeEmpty();
        }
    }

    [Fact]
    public void Should_Handle_Multiple_Resources_With_Different_Tag_Compliance()
    {
        // Arrange
        var mixedResources = FakeDataFactory.CreateMixedGovernanceScenarios();

        // Act
        var findings = _analyzer.AnalyzeAsync(mixedResources).Result;

        // Assert
        findings.Should().HaveCount(2); // Dois recursos sem compliance
        
        var vmFinding = findings.FirstOrDefault(f => f.ResourceName == "vm-without-tags");
        var diskFinding = findings.FirstOrDefault(f => f.ResourceName == "disk-partial-tags");
        
        vmFinding.Should().NotBeNull();
        diskFinding.Should().NotBeNull();
    }
}
