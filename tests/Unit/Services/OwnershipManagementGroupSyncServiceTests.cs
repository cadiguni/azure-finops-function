using FluentAssertions;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Xunit;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Services;

public class OwnershipManagementGroupSyncServiceTests
{
    [Fact]
    public void Reconcile_ShouldCreateMissingTeamsAndPreserveManualAssignments()
    {
        var synchronizedAt = new DateTime(2026, 9, 9, 15, 0, 0, DateTimeKind.Utc);
        var config = new OwnershipConfig
        {
            Teams =
            {
                new TeamOwnership
                {
                    Id = "manual-team",
                    Name = "Manual Team",
                    Subscriptions =
                    {
                        new SubscriptionOwnership
                        {
                            SubscriptionId = "sub-manual",
                            SubscriptionName = "Manual Subscription",
                            Source = "manual"
                        }
                    }
                }
            }
        };
        var subscriptions = new[] { "sub-new", "sub-second", "sub-manual" };
        var parents = new Dictionary<string, ParentManagementGroupInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["sub-new"] = new() { ManagementGroupId = "mg-platform", ManagementGroupDisplayName = "Platform" },
            ["sub-second"] = new() { ManagementGroupId = "mg-platform", ManagementGroupDisplayName = "Platform" },
            ["sub-manual"] = new() { ManagementGroupId = "mg-data", ManagementGroupDisplayName = "Data" }
        };
        var details = new Dictionary<string, object>
        {
            ["sub-new"] = new { display_name = "New Subscription" },
            ["sub-second"] = new { display_name = "Second Subscription" }
        };

        var result = OwnershipManagementGroupSyncService.Reconcile(
            config,
            subscriptions,
            parents,
            details,
            synchronizedAt);

        result.Added.Should().Be(2);
        result.CreatedTeams.Should().Be(1);
        result.SkippedAlreadyAssigned.Should().Be(1);
        result.Conflicts.Should().Be(1);
        result.ConflictDetails.Should().ContainSingle(conflict =>
            conflict.SubscriptionId == "sub-manual"
            && conflict.CurrentTeamId == "manual-team"
            && conflict.SuggestedTeamId == string.Empty
            && conflict.ManagementGroupId == "mg-data");
        result.SkippedWithoutTeamMatch.Should().Be(0);
        config.Teams.Single(team => team.Id == "mg-platform").Subscriptions.Should().HaveCount(2);
        config.Teams.Single(team => team.Id == "manual-team").Subscriptions.Should().ContainSingle();
        config.Teams.Should().NotContain(team => team.Id == "mg-data");
        config.LastManagementGroupSyncAt.Should().Be(synchronizedAt);
    }

    [Fact]
    public void Reconcile_WhenRepeated_ShouldNotDuplicateTeamsOrSubscriptions()
    {
        var config = new OwnershipConfig();
        var subscriptions = new[] { "sub-one" };
        var parents = new Dictionary<string, ParentManagementGroupInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["sub-one"] = new() { ManagementGroupId = "mg-platform", ManagementGroupDisplayName = "Platform" }
        };
        var details = new Dictionary<string, object>
        {
            ["sub-one"] = new { display_name = "Subscription One" }
        };

        OwnershipManagementGroupSyncService.Reconcile(
            config,
            subscriptions,
            parents,
            details,
            DateTime.UtcNow.AddMinutes(-1));
        var secondResult = OwnershipManagementGroupSyncService.Reconcile(
            config,
            subscriptions,
            parents,
            details,
            DateTime.UtcNow);

        secondResult.Added.Should().Be(0);
        secondResult.CreatedTeams.Should().Be(0);
        secondResult.SkippedAlreadyAssigned.Should().Be(1);
        secondResult.Conflicts.Should().Be(0);
        config.Teams.Should().ContainSingle();
        config.Teams.Single().Subscriptions.Should().ContainSingle();
    }

    [Fact]
    public void Reconcile_ShouldMergeLegacyGeneratedMpnTeamIntoExistingBusinessTeam()
    {
        var config = new OwnershipConfig
        {
            Teams =
            {
                new TeamOwnership { Id = "mobile", Name = "Mobile" },
                new TeamOwnership
                {
                    Id = "MPN_MOBILE",
                    Name = "MPN - MOBILE",
                    Subscriptions =
                    {
                        new SubscriptionOwnership
                        {
                            SubscriptionId = "sub-mobile",
                            SubscriptionName = "MPN - ECMEDEIROS - MOBILE",
                            Source = "managementGroup"
                        }
                    }
                }
            }
        };
        var subscriptions = new[] { "sub-mobile" };
        var parents = new Dictionary<string, ParentManagementGroupInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["sub-mobile"] = new()
            {
                ManagementGroupId = "MPN_MOBILE",
                ManagementGroupDisplayName = "MPN - MOBILE"
            }
        };

        var result = OwnershipManagementGroupSyncService.Reconcile(
            config,
            subscriptions,
            parents,
            new Dictionary<string, object>(),
            DateTime.UtcNow);

        result.MovedAutomaticAssignments.Should().Be(1);
        result.RemovedAutomaticTeams.Should().Be(1);
        result.Conflicts.Should().Be(0);
        config.Teams.Should().ContainSingle();
        config.Teams.Single().Id.Should().Be("mobile");
        config.Teams.Single().ManagementGroupIds.Should().Contain("MPN_MOBILE");
        config.Teams.Single().Subscriptions.Should().ContainSingle(subscription =>
            subscription.SubscriptionId == "sub-mobile");
    }

    [Fact]
    public void Reconcile_ShouldNotReportAcceptedManualOverrideAsConflict()
    {
        var config = new OwnershipConfig
        {
            Teams =
            {
                new TeamOwnership
                {
                    Id = "mobile",
                    Name = "Mobile",
                    Subscriptions =
                    {
                        new SubscriptionOwnership
                        {
                            SubscriptionId = "sub-mobile",
                            SubscriptionName = "GVmobile",
                            Source = "manual",
                            ManagementGroupOverride = true
                        }
                    }
                },
                new TeamOwnership { Id = "setores", Name = "Setores" }
            }
        };
        var parents = new Dictionary<string, ParentManagementGroupInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["sub-mobile"] = new()
            {
                ManagementGroupId = "setores",
                ManagementGroupDisplayName = "Setores"
            }
        };

        var result = OwnershipManagementGroupSyncService.Reconcile(
            config,
            new[] { "sub-mobile" },
            parents,
            new Dictionary<string, object>(),
            DateTime.UtcNow);

        result.SkippedAlreadyAssigned.Should().Be(1);
        result.Conflicts.Should().Be(0);
        result.ConflictDetails.Should().BeEmpty();
        config.Teams.Single(team => team.Id == "mobile").Subscriptions.Should().ContainSingle();
    }
}