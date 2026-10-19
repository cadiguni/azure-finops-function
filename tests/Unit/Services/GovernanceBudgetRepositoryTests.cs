using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class GovernanceBudgetRepositoryTests
{
    private const string Id = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void CreateUsesIfNoneMatch_AndUpdateUsesExactETag()
    {
        var create = BlobGovernanceBudgetRepository.WriteConditions(null);
        create.IfNoneMatch.Should().Be(ETag.All);
        create.IfMatch.Should().BeNull();
        var update = BlobGovernanceBudgetRepository.WriteConditions("\"v1\"");
        update.IfMatch.Should().Be(new ETag("\"v1\""));
        update.IfNoneMatch.Should().BeNull();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("v1")]
    [InlineData("\"\"")]
    [InlineData("\"v\r\n1\"")]
    [InlineData("\"v\"1\"")]
    public void InvalidVersionCannotEnableUnconditionalWrites(string version) =>
        FluentActions.Invoking(() => BlobGovernanceBudgetRepository.WriteConditions(version)).Should().Throw<ArgumentException>();

    [Fact]
    public async Task MissingBlobReturnsNull_ButMissingContainerDoesNot()
    {
        var (repository, blob) = Create();
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "missing", "BlobNotFound", null));
        (await repository.GetAsync(Id)).Should().BeNull();
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "missing", "ContainerNotFound", null));
        await FluentActions.Invoking(() => repository.GetAsync(Id)).Should().ThrowAsync<RequestFailedException>();
    }

    [Fact]
    public async Task SavePassesConditionalWriteAndDoesNotSwallowConflict()
    {
        var (repository, blob) = Create();
        BlobUploadOptions? captured = null;
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .Callback<BinaryData, BlobUploadOptions, CancellationToken>((_, options, _) => captured = options)
            .ThrowsAsync(new RequestFailedException(412, "conflict"));
        var budget = new GovernanceBudget(Id, new("test", Id, "2026-09", "BRL", 100m), DateTimeOffset.UtcNow, "admin", "");
        await FluentActions.Invoking(() => repository.SaveAsync(budget, "\"v1\"")).Should().ThrowAsync<RequestFailedException>();
        captured!.Conditions.IfMatch.Should().Be(new ETag("\"v1\""));
    }

    [Fact]
    public async Task PathTraversalRejectedWithoutStorageCall()
    {
        var (repository, blob) = Create();
        await FluentActions.Invoking(() => repository.GetAsync("../other")).Should().ThrowAsync<ArgumentException>();
        blob.VerifyNoOtherCalls();
    }

    [Fact]
    public void MissingContainerConfigurationFailsExplicitly() =>
        FluentActions.Invoking(() => new BlobGovernanceBudgetRepository(Mock.Of<BlobServiceClient>(), new ConfigurationBuilder().Build()))
            .Should().Throw<InvalidOperationException>();

    [Fact]
    public async Task ListingCountsNonJsonEntriesTowardBound_WithoutDownloadingThem()
    {
        var container = new Mock<BlobContainerClient>(MockBehavior.Strict);
        var items = Enumerable.Range(0, 101).Select(i => BlobsModelFactory.BlobItem(name: $"operational/budgets/{i}.txt")).ToArray();
        container.Setup(c => c.GetBlobsAsync(It.IsAny<BlobTraits>(), It.IsAny<BlobStates>(), "operational/budgets/", It.IsAny<CancellationToken>()))
            .Returns(AsyncPageable<BlobItem>.FromPages([Page<BlobItem>.FromValues(items, null, Mock.Of<Response>())]));
        var service = new Mock<BlobServiceClient>();
        service.Setup(s => s.GetBlobContainerClient("configured-container")).Returns(container.Object);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CONFIG_CONTAINER_NAME"] = "configured-container" }).Build();
        await FluentActions.Invoking(() => new BlobGovernanceBudgetRepository(service.Object, config).ListAsync())
            .Should().ThrowAsync<InvalidOperationException>();
        container.Verify(c => c.GetBlobClient(It.IsAny<string>()), Times.Never);
    }

    private static (BlobGovernanceBudgetRepository, Mock<BlobClient>) Create()
    {
        var blob = new Mock<BlobClient>(MockBehavior.Strict);
        var container = new Mock<BlobContainerClient>(MockBehavior.Strict);
        container.Setup(c => c.GetBlobClient("operational/budgets/" + Id + ".json")).Returns(blob.Object);
        var service = new Mock<BlobServiceClient>(MockBehavior.Strict);
        service.Setup(s => s.GetBlobContainerClient("configured-container")).Returns(container.Object);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CONFIG_CONTAINER_NAME"] = "configured-container" }).Build();
        return (new(service.Object, config), blob);
    }
}