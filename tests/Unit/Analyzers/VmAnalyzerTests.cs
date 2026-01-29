using FluentAssertions;
using Gvdasa.FinOpsApi.Domain.Analyzers;
using Gvdasa.FinOpsApi.UnitTests.Fixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Gvdasa.FinOpsApi.UnitTests.Analyzers;

/// <summary>
/// Testes para VmAnalyzer
/// </summary>
public class VmAnalyzerTests
{
    private readonly Mock<ILogger<VmAnalyzer>> _loggerMock;
    private readonly VmAnalyzer _analyzer;

    public VmAnalyzerTests()
    {
        _loggerMock = new Mock<ILogger<VmAnalyzer>>();
        _analyzer = new VmAnalyzer(_loggerMock.Object);
    }

    [Fact]
    public void Should_Identify_Idle_Vm_And_Calculate_Saving()
    {
        // Arrange
        var idleVmUsage = FakeDataFactory.CreateIdleVmUsage();

        // Act
        var findings = _analyzer.AnalyzeAsync(idleVmUsage).Result;

        // Assert
        findings.Should().HaveCount(1);

        var finding = findings.First();
        finding.Category.Should().Be("Compute");
        finding.Severity.Should().Be("High");
        finding.ResourceName.Should().Be("vm-idle-01");
        finding.Title.Should().Contain("baixo uso");
        finding.PotentialMonthlySaving.Should().BeGreaterThan(0);
        
        // Verificar métricas capturadas
        finding.Metrics.Should().ContainKey("CpuPercentage");
        finding.Metrics.Should().ContainKey("MemoryPercentage");
        finding.Metrics["CpuPercentage"].Should().Be(1.2);
    }

    [Fact]
    public void Should_Not_Flag_Production_Vm_With_High_Usage()
    {
        // Arrange
        var productionVmUsage = FakeDataFactory.CreateProductionVmUsage();

        // Act
        var findings = _analyzer.AnalyzeAsync(productionVmUsage).Result;

        // Assert
        findings.Should().BeEmpty("VM de produção com alta utilização não deve ser sinalizada");
    }

    [Fact]
    public void Should_Calculate_Correct_Monthly_Saving_Estimation()
    {
        // Arrange
        var idleVmUsage = FakeDataFactory.CreateIdleVmUsage();
        var expectedDailyCost = 26.67m; // ~800 BRL/mês
        var expectedMonthlySaving = expectedDailyCost * 30 * 0.8m; // 80% de economia

        // Act
        var findings = _analyzer.AnalyzeAsync(idleVmUsage).Result;

        // Assert
        var finding = findings.First();
        finding.PotentialMonthlySaving.Should().BeApproximately((double)expectedMonthlySaving, 50);
    }

    [Fact]
    public void Should_Provide_Actionable_Recommendation()
    {
        // Arrange
        var idleVmUsage = FakeDataFactory.CreateIdleVmUsage();

        // Act
        var findings = _analyzer.AnalyzeAsync(idleVmUsage).Result;

        // Assert
        var finding = findings.First();
        finding.Recommendation.Should().NotBeNullOrEmpty();
        finding.Recommendation.Should().Contain("redimensionar");
        finding.Impact.Should().Be("Alto");
    }

    [Theory]
    [InlineData(1.0, 5.0, true)]  // Muito baixo CPU e memória
    [InlineData(2.5, 8.0, true)]  // CPU no limite, mas memória baixa
    [InlineData(10.0, 15.0, false)] // Uso normal
    [InlineData(50.0, 60.0, false)] // Uso alto
    public void Should_Apply_Correct_Thresholds(double cpuPercent, double memoryPercent, bool shouldFlag)
    {
        // Arrange
        var vmUsage = FakeDataFactory.CreateIdleVmUsage();
        vmUsage[0].CpuPercentage = cpuPercent;
        vmUsage[0].MemoryPercentage = memoryPercent;

        // Act
        var findings = _analyzer.AnalyzeAsync(vmUsage).Result;

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
}