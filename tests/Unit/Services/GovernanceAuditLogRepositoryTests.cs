using System.Collections.Concurrent;
using System.Text.Json;
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

public class GovernanceAuditLogRepositoryTests
{
    private const string Entity = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task ConcurrentEvents_AreSeparateConditionalBlobs_NotReadModifyWrite()
    {
        var keys = new ConcurrentBag<string>();
        var events = new ConcurrentBag<string>();
        var blob = new Mock<BlobClient>(MockBehavior.Strict);
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .Callback<BinaryData, BlobUploadOptions, CancellationToken>((body, options, _) =>
            {
                options.Conditions.IfNoneMatch.Should().Be(ETag.All);
                options.Conditions.IfMatch.Should().BeNull();
                events.Add(body.ToString());
            }).ReturnsAsync(Response.FromValue((BlobContentInfo)null!, Mock.Of<Response>()));
        var container = new Mock<BlobContainerClient>(MockBehavior.Strict);
        container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Callback<string>(keys.Add).Returns(blob.Object);
        var client = new Mock<BlobServiceClient>(MockBehavior.Strict);
        client.Setup(c => c.GetBlobContainerClient("configured-container")).Returns(container.Object);
        var repository = new GovernanceAuditLogRepository(client.Object, Config());
        var time = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => repository.AppendAsync(new AuditLogDocument
        {
            EntityId = Entity, EntityType = "governanceBudget", Action = "Delete", PerformedAt = time,
            PerformedBy = "token-oid", Metadata = new() { ["outcome"] = "Succeeded", ["expectedVersion"] = "\"v1\"" }
        })));
        keys.Should().HaveCount(20).And.OnlyHaveUniqueItems();
        keys.Should().OnlyContain(k => k.StartsWith("audit-custom/" + Entity + "/"));
        foreach (var body in events)
        {
            using var json = JsonDocument.Parse(body);
            json.RootElement.GetProperty("performedBy").GetString().Should().Be("token-oid");
            json.RootElement.GetProperty("action").GetString().Should().Be("Delete");
            json.RootElement.GetProperty("metadata").GetProperty("outcome").GetString().Should().Be("Succeeded");
        }
        // Strict mocks reject container creation, downloads, listing and all non-append writes.
    }

    [Fact]
    public async Task StorageFailure_PropagatesInsteadOfSilentlyDroppingEvent()
    {
        var blob = new Mock<BlobClient>(MockBehavior.Strict);
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "unavailable"));
        var container = new Mock<BlobContainerClient>();
        container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        var client = new Mock<BlobServiceClient>();
        client.Setup(c => c.GetBlobContainerClient("configured-container")).Returns(container.Object);
        await FluentActions.Invoking(() => new GovernanceAuditLogRepository(client.Object, Config())
            .AppendAsync(new AuditLogDocument { EntityId = Entity })).Should().ThrowAsync<RequestFailedException>();
    }

    [Fact]
    public async Task InvalidEntityId_CannotEscapePrefix()
    {
        var container = new Mock<BlobContainerClient>(MockBehavior.Strict);
        var client = new Mock<BlobServiceClient>();
        client.Setup(c => c.GetBlobContainerClient("configured-container")).Returns(container.Object);
        var repository = new GovernanceAuditLogRepository(client.Object, Config());
        await FluentActions.Invoking(() => repository.AppendAsync(new AuditLogDocument { EntityId = "../other" }))
            .Should().ThrowAsync<ArgumentException>();
        container.VerifyNoOtherCalls();
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["CONFIG_CONTAINER_NAME"] = "configured-container", ["GovernanceAuditBlobPrefix"] = "audit-custom"
    }).Build();
}