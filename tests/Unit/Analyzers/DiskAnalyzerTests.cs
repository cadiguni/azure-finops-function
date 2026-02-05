using FluentAssertions;
using Personal.FinOpsApi.Domain.Analyzers;
using Personal.FinOpsApi.UnitTests.Fixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.UnitTests.Analyzers;

/// <summary>
/// Testes para DiskAnalyzer
/// </summary>
public class DiskAnalyzerTests
{
    private readonly Mock<ILogger<DiskAnalyzer>> _loggerMock;
    private readonly DiskAnalyzer _analyzer;

    public DiskAnalyzerTests()
    {
        _loggerMock = new Mock<ILogger<DiskAnalyzer>>();
        _analyzer = new DiskAnalyzer(_loggerMock.Object);
    }

    [Fact]
    public void Should_Identify_Unattached_Disk_And_Calculate_Full_Cost_Saving()
    {
        // Arrange
        var unattachedDiskUsage = FakeDataFactory.CreateUnattachedDiskUsage();

        // Act
        var findings = _analyzer.AnalyzeAsync(unattachedDiskUsage).Result;

        // Assert
        findings.Should().HaveCount(1);

        var finding = findings.First();
        finding.Category.Should().Be("Storage");
        finding.Severity.Should().Be("Medium");
        finding.ResourceName.Should().Be("disk-unattached-01");
        finding.Title.Should().Contain("não anexado");
        finding.PotentialMonthlySaving.Should().BeApproximately(300, 1); // 10 BRL/dia * 30
    }

    [Fact]
    public void Should_Not_Flag_Attached_Disks()
    {
        // Arrange
        var attachedDiskUsage = FakeDataFactory.CreateAttachedDiskUsage();

        // Act
        var findings = _analyzer.AnalyzeAsync(attachedDiskUsage).Result;

        // Assert
        findings.Should().BeEmpty("Discos anexados não devem ser sinalizados");
    }

    [Theory]
    [InlineData(true, 1)]  // Unattached -> should flag
    [InlineData(false, 0)] // Attached -> should not flag
    public void Should_Handle_Attachment_Status_Correctly(bool isUnattached, int expectedFindings)
    {
        // Arrange
        var diskUsage = FakeDataFactory.CreateUnattachedDiskUsage();
        diskUsage[0].IsAttached = !isUnattached;

        // Act
        var findings = _analyzer.AnalyzeAsync(diskUsage).Result;

        // Assert
        findings.Should().HaveCount(expectedFindings);
    }

    [Fact]
    public void Should_Recommend_Immediate_Action_For_High_Cost_Unattached_Disk()
    {
        // Arrange
        var expensiveUnattachedDisk = FakeDataFactory.CreateExpensiveUnattachedDiskUsage();

        // Act
        var findings = _analyzer.AnalyzeAsync(expensiveUnattachedDisk).Result;

        // Assert
        var finding = findings.First();
        finding.Recommendation.Should().Contain("imediatamente");
        finding.Impact.Should().Be("Médio");
        finding.PotentialMonthlySaving.Should().BeGreaterThan(500); // Alto custo
    }

    [Fact]
    public void Should_Include_Disk_Size_And_Type_In_Analysis()
    {
        // Arrange
        var unattachedDiskUsage = FakeDataFactory.CreateUnattachedDiskUsage();

        // Act
        var findings = _analyzer.AnalyzeAsync(unattachedDiskUsage).Result;

        // Assert
        var finding = findings.First();
        finding.Metrics.Should().ContainKey("DiskSizeGB");
        finding.Metrics.Should().ContainKey("DiskType");
        finding.Metrics["DiskSizeGB"].Should().Be(128);
        finding.Metrics["DiskType"].Should().Be("Premium_LRS");
    }
}
