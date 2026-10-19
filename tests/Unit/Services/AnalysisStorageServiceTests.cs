using System.Reflection;
using Azure.Storage.Blobs;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class AnalysisStorageServiceTests
{
    [Fact]
    public void PayloadQuality_WhenCandidateLosesCostsToUnavailable_ShouldPreserveExisting()
    {
        const string existing = """
        {"recommendations":[{"observedCost":12.3,"costProvenance":{"source":"CostManagementExport"}}]}
        """;
        const string candidate = """
        {"recommendations":[{"observedCost":null,"estimatedMonthlyCost":0,"costProvenance":{"source":"Unavailable"}}]}
        """;

        var existingQuality = AnalysisStorageService.EvaluateRecommendationPayload(existing);
        var candidateQuality = AnalysisStorageService.EvaluateRecommendationPayload(candidate);

        existingQuality.AvailableCostCount.Should().Be(1);
        candidateQuality.UnavailableCostCount.Should().Be(1);
        AnalysisStorageService.ShouldPreserveExistingPayload(existingQuality, candidateQuality).Should().BeTrue();
    }

    [Fact]
    public void PayloadQuality_WhenCandidateHasValidZeroObservedCost_ShouldAllowUpdate()
    {
        const string existing = """
        {"recommendations":[{"observedCost":12.3,"costProvenance":{"source":"CostManagementExport"}}]}
        """;
        const string candidate = """
        {"recommendations":[{"observedCost":0,"estimatedMonthlyCost":0,"costProvenance":{"source":"CostManagementExport"}}]}
        """;

        var existingQuality = AnalysisStorageService.EvaluateRecommendationPayload(existing);
        var candidateQuality = AnalysisStorageService.EvaluateRecommendationPayload(candidate);

        candidateQuality.AvailableCostCount.Should().Be(1);
        AnalysisStorageService.ShouldPreserveExistingPayload(existingQuality, candidateQuality).Should().BeFalse();
    }

    [Fact]
    public void NormalizeSubscriptionId_WhenGuid_ShouldReturnSameGuid()
    {
        var sut = BuildService();
        var guid = "00000000-0000-0000-0000-00000000a004";

        var normalized = InvokeNormalizeSubscriptionId(sut, guid);

        normalized.Should().Be(guid);
    }

    [Fact]
    public void NormalizeSubscriptionId_WhenJsonPayload_ShouldExtractSubscriptionId()
    {
        var sut = BuildService();
        var payload = """
        {"SubscriptionId":"00000000-0000-0000-0000-00000000a004","AnalysisType":"manual-test"}
        """;

        var normalized = InvokeNormalizeSubscriptionId(sut, payload);

        normalized.Should().Be("00000000-0000-0000-0000-00000000a004");
    }

    [Fact]
    public void NormalizeSubscriptionId_WhenInvalidText_ShouldReturnSanitizedValue()
    {
        var sut = BuildService();
        var text = """{"foo":"bar"}_@@@subscription??""";

        var normalized = InvokeNormalizeSubscriptionId(sut, text);

        normalized.Should().NotBeNullOrWhiteSpace();
        normalized.All(char.IsLetterOrDigit).Should().BeTrue();
        normalized.Length.Should().BeLessOrEqualTo(64);
    }

    private static AnalysisStorageService BuildService()
    {
        var blobServiceClient = new Mock<BlobServiceClient>();
        var containerClient = new Mock<BlobContainerClient>();
        blobServiceClient
            .Setup(c => c.GetBlobContainerClient(It.IsAny<string>()))
            .Returns(containerClient.Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RESULTS_CONTAINER_NAME"] = "finops-analysis"
            })
            .Build();

        return new AnalysisStorageService(
            blobServiceClient.Object,
            new NullLogger<AnalysisStorageService>(),
            config);
    }

    private static string InvokeNormalizeSubscriptionId(AnalysisStorageService sut, string value)
    {
        var method = typeof(AnalysisStorageService)
            .GetMethod("NormalizeSubscriptionId", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        var result = method!.Invoke(sut, new object[] { value });
        return result as string ?? string.Empty;
    }
}
