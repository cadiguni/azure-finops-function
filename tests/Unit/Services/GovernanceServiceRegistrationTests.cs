using Azure.Storage.Blobs;
using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class GovernanceServiceRegistrationTests
{
    [Fact]
    public void RegistrationResolvesBudgetAndHealthWithDedicatedAuditWithoutAzureIo()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["CONFIG_CONTAINER_NAME"] = "config" }).Build());
        var blobs = new Mock<BlobServiceClient>(MockBehavior.Strict);
        blobs.Setup(b => b.GetBlobContainerClient("config")).Returns(new Mock<BlobContainerClient>(MockBehavior.Strict).Object);
        services.AddSingleton(blobs.Object);
        services.AddSingleton(new Mock<ICostExportVersionStore>(MockBehavior.Strict).Object);
        services.AddSingleton(new Mock<ICostStorageRepository>(MockBehavior.Strict).Object);
        services.AddSingleton(new Mock<Personal.FinOpsApi.AzureFunctions.Services.Repositories.ICostAnomalyReportRepository>(MockBehavior.Strict).Object);
        services.AddFinOpsGovernance();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<GovernanceBudgetService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<GovernanceDataHealthService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<GovernanceDataHealthOverviewService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IGovernanceAuditLogRepository>().Should().BeOfType<GovernanceAuditLogRepository>();
    }
}
