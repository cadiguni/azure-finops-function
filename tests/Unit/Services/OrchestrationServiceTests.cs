using FluentAssertions;
using Gvdasa.FinOpsApi.Domain.Services;
using Gvdasa.FinOpsApi.Domain.Analyzers;
using Gvdasa.FinOpsApi.UnitTests.Fixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Gvdasa.FinOpsApi.UnitTests.Services;

/// <summary>
/// Testes de integração para OrchestrationService
/// Testa o fluxo completo de orquestração dos analyzers
/// </summary>
public class OrchestrationServiceTests
{
    private readonly Mock<ILogger<OrchestrationService>> _loggerMock;
    private readonly Mock<IVmAnalyzer> _vmAnalyzerMock;
    private readonly Mock<IDiskAnalyzer> _diskAnalyzerMock;
    private readonly Mock<IAppServiceAnalyzer> _appServiceAnalyzerMock;
    private readonly Mock<IGovernanceTagsAnalyzer> _governanceAnalyzerMock;
    private readonly Mock<IEnvironmentClassificationAnalyzer> _environmentAnalyzerMock;
    private readonly OrchestrationService _orchestrationService;

    public OrchestrationServiceTests()
    {
        _loggerMock = new Mock<ILogger<OrchestrationService>>();
        _vmAnalyzerMock = new Mock<IVmAnalyzer>();
        _diskAnalyzerMock = new Mock<IDiskAnalyzer>();
        _appServiceAnalyzerMock = new Mock<IAppServiceAnalyzer>();
        _governanceAnalyzerMock = new Mock<IGovernanceTagsAnalyzer>();
        _environmentAnalyzerMock = new Mock<IEnvironmentClassificationAnalyzer>();

        _orchestrationService = new OrchestrationService(
            _loggerMock.Object,
            _vmAnalyzerMock.Object,
            _diskAnalyzerMock.Object,
            _appServiceAnalyzerMock.Object,
            _governanceAnalyzerMock.Object,
            _environmentAnalyzerMock.Object
        );
    }

    [Fact]
    public async Task Should_Execute_All_Analyzers_And_Combine_Results()
    {
        // Arrange
        var vmUsage = FakeDataFactory.CreateIdleVmUsage();
        var diskUsage = FakeDataFactory.CreateUnattachedDiskUsage();
        var appServiceUsage = FakeDataFactory.CreateLowTrafficAppServiceUsage();
        var resourcesWithMissingTags = FakeDataFactory.CreateResourcesWithMissingTags();
        var mixedEnvironments = FakeDataFactory.CreateMixedEnvironmentScenarios();

        // Mock analyzer results
        var vmFindings = FakeDataFactory.CreateVmOptimizationFindings();
        var diskFindings = FakeDataFactory.CreateDiskOptimizationFindings();
        var appServiceFindings = FakeDataFactory.CreateAppServiceOptimizationFindings();
        var governanceFindings = FakeDataFactory.CreateGovernanceFindings();
        var environmentFindings = FakeDataFactory.CreateEnvironmentClassificationFindings();

        _vmAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<VmUsage>>()))
                      .ReturnsAsync(vmFindings);
        
        _diskAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<DiskUsage>>()))
                        .ReturnsAsync(diskFindings);
        
        _appServiceAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<AppServiceUsage>>()))
                              .ReturnsAsync(appServiceFindings);
        
        _governanceAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<ResourceUsage>>()))
                              .ReturnsAsync(governanceFindings);
        
        _environmentAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<ResourceUsage>>()))
                               .ReturnsAsync(environmentFindings);

        // Act
        var result = await _orchestrationService.ExecuteAnalysisAsync(
            vmUsage, 
            diskUsage, 
            appServiceUsage, 
            resourcesWithMissingTags);

        // Assert
        result.Should().NotBeNull();
        result.TotalFindings.Should().Be(5); // 1 de cada analyzer
        result.TotalPotentialSavings.Should().BeGreaterThan(0);
        
        // Verificar que todos os analyzers foram chamados
        _vmAnalyzerMock.Verify(x => x.AnalyzeAsync(It.IsAny<IEnumerable<VmUsage>>()), Times.Once);
        _diskAnalyzerMock.Verify(x => x.AnalyzeAsync(It.IsAny<IEnumerable<DiskUsage>>()), Times.Once);
        _appServiceAnalyzerMock.Verify(x => x.AnalyzeAsync(It.IsAny<IEnumerable<AppServiceUsage>>()), Times.Once);
        _governanceAnalyzerMock.Verify(x => x.AnalyzeAsync(It.IsAny<IEnumerable<ResourceUsage>>()), Times.Once);
        _environmentAnalyzerMock.Verify(x => x.AnalyzeAsync(It.IsAny<IEnumerable<ResourceUsage>>()), Times.Once);
    }

    [Fact]
    public async Task Should_Calculate_Total_Savings_Correctly()
    {
        // Arrange
        var vmUsage = FakeDataFactory.CreateIdleVmUsage();
        var diskUsage = FakeDataFactory.CreateUnattachedDiskUsage();
        var appServiceUsage = FakeDataFactory.CreateLowTrafficAppServiceUsage();
        var resources = FakeDataFactory.CreateResourcesWithMissingTags();

        // Mock findings com valores específicos
        var vmFindings = new List<OptimizationFinding>
        {
            new() { PotentialMonthlySaving = 800 }
        };
        var diskFindings = new List<OptimizationFinding>
        {
            new() { PotentialMonthlySaving = 300 }
        };
        var appServiceFindings = new List<OptimizationFinding>
        {
            new() { PotentialMonthlySaving = 400 }
        };

        _vmAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<VmUsage>>()))
                      .ReturnsAsync(vmFindings);
        _diskAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<DiskUsage>>()))
                        .ReturnsAsync(diskFindings);
        _appServiceAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<AppServiceUsage>>()))
                              .ReturnsAsync(appServiceFindings);
        _governanceAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<ResourceUsage>>()))
                              .ReturnsAsync(new List<OptimizationFinding>());
        _environmentAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<ResourceUsage>>()))
                               .ReturnsAsync(new List<OptimizationFinding>());

        // Act
        var result = await _orchestrationService.ExecuteAnalysisAsync(vmUsage, diskUsage, appServiceUsage, resources);

        // Assert
        result.TotalPotentialSavings.Should().Be(1500); // 800 + 300 + 400
    }

    [Fact]
    public async Task Should_Categorize_Findings_By_Severity()
    {
        // Arrange
        var vmUsage = FakeDataFactory.CreateIdleVmUsage();
        var diskUsage = FakeDataFactory.CreateUnattachedDiskUsage();
        var appServiceUsage = FakeDataFactory.CreateLowTrafficAppServiceUsage();
        var resources = FakeDataFactory.CreateResourcesWithMissingTags();

        var findings = new List<OptimizationFinding>
        {
            new() { Severity = "High", Category = "Compute" },
            new() { Severity = "Medium", Category = "Storage" },
            new() { Severity = "Low", Category = "Governance" }
        };

        _vmAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<VmUsage>>()))
                      .ReturnsAsync(findings.Where(f => f.Category == "Compute"));
        _diskAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<DiskUsage>>()))
                        .ReturnsAsync(findings.Where(f => f.Category == "Storage"));
        _appServiceAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<AppServiceUsage>>()))
                              .ReturnsAsync(new List<OptimizationFinding>());
        _governanceAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<ResourceUsage>>()))
                              .ReturnsAsync(findings.Where(f => f.Category == "Governance"));
        _environmentAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<ResourceUsage>>()))
                               .ReturnsAsync(new List<OptimizationFinding>());

        // Act
        var result = await _orchestrationService.ExecuteAnalysisAsync(vmUsage, diskUsage, appServiceUsage, resources);

        // Assert
        result.FindingsBySeverity["High"].Should().Be(1);
        result.FindingsBySeverity["Medium"].Should().Be(1);
        result.FindingsBySeverity["Low"].Should().Be(1);
    }

    [Fact]
    public async Task Should_Group_Findings_By_Category()
    {
        // Arrange
        var vmUsage = FakeDataFactory.CreateIdleVmUsage();
        var diskUsage = FakeDataFactory.CreateUnattachedDiskUsage();
        var appServiceUsage = FakeDataFactory.CreateLowTrafficAppServiceUsage();
        var resources = FakeDataFactory.CreateResourcesWithMissingTags();

        var computeFindings = new List<OptimizationFinding>
        {
            new() { Category = "Compute", Title = "VM Finding 1" },
            new() { Category = "Compute", Title = "VM Finding 2" }
        };

        var storageFindings = new List<OptimizationFinding>
        {
            new() { Category = "Storage", Title = "Disk Finding 1" }
        };

        _vmAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<VmUsage>>()))
                      .ReturnsAsync(computeFindings);
        _diskAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<DiskUsage>>()))
                        .ReturnsAsync(storageFindings);
        _appServiceAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<AppServiceUsage>>()))
                              .ReturnsAsync(new List<OptimizationFinding>());
        _governanceAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<ResourceUsage>>()))
                              .ReturnsAsync(new List<OptimizationFinding>());
        _environmentAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<ResourceUsage>>()))
                               .ReturnsAsync(new List<OptimizationFinding>());

        // Act
        var result = await _orchestrationService.ExecuteAnalysisAsync(vmUsage, diskUsage, appServiceUsage, resources);

        // Assert
        result.FindingsByCategory["Compute"].Should().Be(2);
        result.FindingsByCategory["Storage"].Should().Be(1);
    }

    [Fact]
    public async Task Should_Handle_Analyzer_Exceptions_Gracefully()
    {
        // Arrange
        var vmUsage = FakeDataFactory.CreateIdleVmUsage();
        var diskUsage = FakeDataFactory.CreateUnattachedDiskUsage();
        var appServiceUsage = FakeDataFactory.CreateLowTrafficAppServiceUsage();
        var resources = FakeDataFactory.CreateResourcesWithMissingTags();

        // Simular falha em um analyzer
        _vmAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<VmUsage>>()))
                      .ThrowsAsync(new InvalidOperationException("VM Analyzer failed"));
        
        _diskAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<DiskUsage>>()))
                        .ReturnsAsync(new List<OptimizationFinding> 
                        { 
                            new() { Title = "Disk finding", PotentialMonthlySaving = 100 } 
                        });
        
        _appServiceAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<AppServiceUsage>>()))
                              .ReturnsAsync(new List<OptimizationFinding>());
        _governanceAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<ResourceUsage>>()))
                              .ReturnsAsync(new List<OptimizationFinding>());
        _environmentAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<ResourceUsage>>()))
                               .ReturnsAsync(new List<OptimizationFinding>());

        // Act
        var result = await _orchestrationService.ExecuteAnalysisAsync(vmUsage, diskUsage, appServiceUsage, resources);

        // Assert
        result.Should().NotBeNull();
        result.TotalFindings.Should().Be(1); // Só o disk analyzer funcionou
        result.HasErrors.Should().BeTrue();
        result.Errors.Should().Contain("VM Analyzer failed");
    }

    [Fact]
    public async Task Should_Generate_Summary_Report()
    {
        // Arrange
        var vmUsage = FakeDataFactory.CreateIdleVmUsage();
        var diskUsage = FakeDataFactory.CreateUnattachedDiskUsage();
        var appServiceUsage = FakeDataFactory.CreateLowTrafficAppServiceUsage();
        var resources = FakeDataFactory.CreateResourcesWithMissingTags();

        _vmAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<VmUsage>>()))
                      .ReturnsAsync(FakeDataFactory.CreateVmOptimizationFindings());
        _diskAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<DiskUsage>>()))
                        .ReturnsAsync(FakeDataFactory.CreateDiskOptimizationFindings());
        _appServiceAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<AppServiceUsage>>()))
                              .ReturnsAsync(FakeDataFactory.CreateAppServiceOptimizationFindings());
        _governanceAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<ResourceUsage>>()))
                              .ReturnsAsync(FakeDataFactory.CreateGovernanceFindings());
        _environmentAnalyzerMock.Setup(x => x.AnalyzeAsync(It.IsAny<IEnumerable<ResourceUsage>>()))
                               .ReturnsAsync(FakeDataFactory.CreateEnvironmentClassificationFindings());

        // Act
        var result = await _orchestrationService.ExecuteAnalysisAsync(vmUsage, diskUsage, appServiceUsage, resources);

        // Assert
        result.Summary.Should().NotBeNullOrEmpty();
        result.Summary.Should().Contain("análise de otimização");
        result.ExecutionTime.Should().BeGreaterThan(TimeSpan.Zero);
        result.AnalysisDate.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }
}