using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Moq;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class OwnershipTeamSubscriptionsLookupServiceTests
{
    [Fact]
    public async Task GetAllSubscriptionTeamMappingsAsync_ShouldPreferEnabledOwnershipAndFallbackForUnknownSubscriptions()
    {
        var ownershipReader = new Mock<IOwnershipConfigReader>(MockBehavior.Strict);
        ownershipReader.Setup(reader => reader.GetConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OwnershipConfig
            {
                Teams =
                {
                    new TeamOwnership
                    {
                        Id = "team-platform",
                        Subscriptions =
                        {
                            new SubscriptionOwnership { SubscriptionId = "sub-shared", Enabled = true },
                            new SubscriptionOwnership { SubscriptionId = "sub-disabled", Enabled = false }
                        }
                    }
                }
            });

        var legacyLookup = new Mock<ILegacyTeamSubscriptionsLookupService>(MockBehavior.Strict);
        legacyLookup.Setup(lookup => lookup.GetAllSubscriptionTeamMappingsAsync())
            .ReturnsAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sub-shared"] = "team-legacy",
                ["sub-disabled"] = "team-legacy",
                ["sub-legacy-only"] = "team-data"
            });

        var service = new OwnershipTeamSubscriptionsLookupService(ownershipReader.Object, legacyLookup.Object);

        var result = await service.GetAllSubscriptionTeamMappingsAsync();

        result.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["sub-shared"] = "team-platform",
            ["sub-legacy-only"] = "team-data"
        });
    }
}