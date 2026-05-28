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
    public void NormalizeSubscriptionId_WhenGuid_ShouldReturnSameGuid()
    {
        var sut = BuildService();
        var guid = "504a622c-3995-46c5-8ba7-8edb365ed17b";

        var normalized = InvokeNormalizeSubscriptionId(sut, guid);

        normalized.Should().Be(guid);
    }

    [Fact]
    public void NormalizeSubscriptionId_WhenJsonPayload_ShouldExtractSubscriptionId()
    {
        var sut = BuildService();
        var payload = """
        {"SubscriptionId":"504a622c-3995-46c5-8ba7-8edb365ed17b","AnalysisType":"manual-test"}
        """;

        var normalized = InvokeNormalizeSubscriptionId(sut, payload);

        normalized.Should().Be("504a622c-3995-46c5-8ba7-8edb365ed17b");
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
