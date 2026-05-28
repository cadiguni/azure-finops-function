using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class AnalyzerContractValidatorTests
{
    [Fact]
    public void ValidateResult_WhenResultIsValid_ShouldReturnNoErrors()
    {
        var result = BuildValidResult();

        var validation = AnalyzerContractValidator.ValidateResult(result);

        validation.IsValid.Should().BeTrue();
        validation.Errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateResult_WhenRequiredFieldsAreMissing_ShouldReturnErrors()
    {
        var result = new StandardAnalyzerResult
        {
            AnalysisId = "",
            Analyzer = "",
            SubscriptionId = "",
            Findings = new List<StandardFinding>
            {
                new()
                {
                    Type = "",
                    ResourceId = "",
                    ResourceName = "",
                    ResourceType = "",
                    ResourceGroup = "",
                    SubscriptionId = "",
                    EstimatedMonthlySavings = -1,
                    Priority = "Urgent",
                    Description = "",
                    Confidence = 2.0
                }
            }
        };

        var validation = AnalyzerContractValidator.ValidateResult(result);

        validation.IsValid.Should().BeFalse();
        validation.Errors.Should().Contain(e => e.Contains("AnalysisId"));
        validation.Errors.Should().Contain(e => e.Contains("Analyzer"));
        validation.Errors.Should().Contain(e => e.Contains("SubscriptionId"));
        validation.Errors.Should().Contain(e => e.Contains("ExecutedAt"));
        validation.Errors.Should().Contain(e => e.Contains("Priority deve ser Low, Medium ou High"));
        validation.Errors.Should().Contain(e => e.Contains("Confidence deve estar entre 0.0 e 1.0"));
    }

    [Fact]
    public void GenerateValidationReport_WhenValid_ShouldContainValidMessage()
    {
        var result = BuildValidResult();

        var report = AnalyzerContractValidator.GenerateValidationReport(result);

        report.Should().Contain("CONTRATO VÁLIDO");
        report.Should().Contain("StorageAnalyzer");
    }

    [Fact]
    public void GenerateValidationReport_WhenInvalid_ShouldContainErrorList()
    {
        var result = BuildValidResult();
        result.Findings[0].Priority = "Invalid";

        var report = AnalyzerContractValidator.GenerateValidationReport(result);

        report.Should().Contain("CONTRATO INVÁLIDO");
        report.Should().Contain("Priority deve ser Low, Medium ou High");
    }

    private static StandardAnalyzerResult BuildValidResult()
    {
        return new StandardAnalyzerResult
        {
            AnalysisId = Guid.NewGuid().ToString(),
            Analyzer = "StorageAnalyzer",
            SubscriptionId = "sub-1",
            ExecutedAt = DateTime.UtcNow,
            Findings = new List<StandardFinding>
            {
                new()
                {
                    Type = FindingTypes.UNDER_UTILIZED_STORAGE_ACCOUNT,
                    ResourceId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/st01",
                    ResourceName = "st01",
                    ResourceType = "Microsoft.Storage/storageAccounts",
                    ResourceGroup = "rg",
                    SubscriptionId = "sub-1",
                    EstimatedMonthlySavings = 30,
                    Priority = FindingPriorities.MEDIUM,
                    Description = "Storage subutilizado",
                    Confidence = 0.8
                }
            }
        };
    }
}
