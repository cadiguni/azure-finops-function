using Personal.FinOpsApi.AzureFunctions.Models;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public sealed class OwnershipManagementGroupSyncService
{
    private readonly OwnershipStorageService _ownershipStorageService;
    private readonly SubscriptionDiscoveryService _subscriptionDiscoveryService;
    private readonly AzureManagementGroupService _managementGroupService;

    public OwnershipManagementGroupSyncService(
        OwnershipStorageService ownershipStorageService,
        SubscriptionDiscoveryService subscriptionDiscoveryService,
        AzureManagementGroupService managementGroupService)
    {
        _ownershipStorageService = ownershipStorageService;
        _subscriptionDiscoveryService = subscriptionDiscoveryService;
        _managementGroupService = managementGroupService;
    }

    public async Task<OwnershipManagementGroupSyncResult> SyncAsync(
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        var config = await _ownershipStorageService.GetConfigAsync(cancellationToken);
        var parentsBySubscription = await _managementGroupService.GetAllSubscriptionParentManagementGroupsAsync();
        var subscriptions = parentsBySubscription.Keys
            .Where(subscriptionId => !string.IsNullOrWhiteSpace(subscriptionId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (subscriptions.Count == 0)
        {
            subscriptions = (await _subscriptionDiscoveryService.DiscoverSubscriptionsAsync())
                .Where(subscriptionId => !string.IsNullOrWhiteSpace(subscriptionId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            parentsBySubscription = await _managementGroupService
                .GetParentManagementGroupsForSubscriptionsAsync(subscriptions);
        }

        var details = await _subscriptionDiscoveryService.GetSubscriptionDetailsAsync(subscriptions);
        var result = Reconcile(config, subscriptions, parentsBySubscription, details, DateTime.UtcNow);
        await _ownershipStorageService.SaveConfigAsync(config, updatedBy, cancellationToken: cancellationToken);
        return result;
    }

    public static OwnershipManagementGroupSyncResult Reconcile(
        OwnershipConfig config,
        IReadOnlyCollection<string> subscriptions,
        IReadOnlyDictionary<string, ParentManagementGroupInfo> parentsBySubscription,
        IReadOnlyDictionary<string, object> details,
        DateTime synchronizedAt)
    {
        var result = new OwnershipManagementGroupSyncResult
        {
            TotalSubscriptionsDiscovered = subscriptions.Count,
            TotalSubscriptionsWithParent = parentsBySubscription.Count
        };

        foreach (var subscriptionId in subscriptions)
        {
            if (!parentsBySubscription.TryGetValue(subscriptionId, out var parent))
            {
                result.SkippedWithoutParent++;
                continue;
            }

            var parentKey = NormalizeKey(parent.ManagementGroupId);
            var parentNameKey = NormalizeKey(parent.ManagementGroupDisplayName);
            var team = ResolveTeam(config.Teams, parent);
            var hasMatchingTeam = team != null;
            var assignedTeam = config.Teams.FirstOrDefault(existingTeam =>
                existingTeam.Subscriptions.Any(subscription =>
                    subscription.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase)));
            if (assignedTeam != null)
            {
                result.SkippedAlreadyAssigned++;
                var assignment = assignedTeam.Subscriptions.First(subscription =>
                    subscription.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase));
                if (assignment.ManagementGroupOverride)
                {
                    continue;
                }

                if (hasMatchingTeam && !assignedTeam.Id.Equals(team!.Id, StringComparison.OrdinalIgnoreCase))
                {
                    if (assignment.Source.Equals("managementGroup", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsAutomaticTeamForParent(assignedTeam, parent))
                        {
                            assignedTeam.Source = "managementGroup";
                        }
                        assignedTeam.Subscriptions.Remove(assignment);
                        team.Subscriptions.Add(assignment);
                        result.MovedAutomaticAssignments++;
                    }
                    else
                    {
                        AddConflict(result, assignment, assignedTeam, team, parent);
                    }
                }
                else if (!hasMatchingTeam)
                {
                    AddConflict(result, assignment, assignedTeam, null, parent);
                }
                continue;
            }

            if (!hasMatchingTeam)
            {
                var teamId = string.IsNullOrWhiteSpace(parent.ManagementGroupId)
                    ? parent.ManagementGroupDisplayName.Trim()
                    : parent.ManagementGroupId.Trim();
                if (string.IsNullOrWhiteSpace(teamId))
                {
                    result.SkippedWithoutTeamMatch++;
                    continue;
                }

                team = new TeamOwnership
                {
                    Id = teamId,
                    Name = string.IsNullOrWhiteSpace(parent.ManagementGroupDisplayName)
                        ? teamId
                        : parent.ManagementGroupDisplayName.Trim(),
                    Source = "managementGroup",
                    ManagementGroupIds = { parent.ManagementGroupId }
                };
                config.Teams.Add(team);
                result.CreatedTeams++;
            }
            else
            {
                AddManagementGroupAlias(team!, parent.ManagementGroupId);
            }

            team!.Subscriptions.Add(new SubscriptionOwnership
            {
                SubscriptionId = subscriptionId,
                SubscriptionName = ExtractSubscriptionName(details, subscriptionId),
                Environment = parent.ManagementGroupDisplayName,
                Enabled = true,
                Source = "managementGroup",
                SyncedAt = synchronizedAt
            });
            result.Added++;
        }

        result.RemovedAutomaticTeams = config.Teams.RemoveAll(team =>
            team.Subscriptions.Count == 0 && IsAutomaticTeam(team));

        config.LastManagementGroupSyncAt = synchronizedAt;
        config.LastManagementGroupSyncAdded = result.Added;
        config.LastManagementGroupSyncCreatedTeams = result.CreatedTeams;
        config.LastManagementGroupSyncConflicts = result.Conflicts;
        return result;
    }

    private static TeamOwnership? ResolveTeam(
        IReadOnlyCollection<TeamOwnership> teams,
        ParentManagementGroupInfo parent)
    {
        var parentId = NormalizeKey(parent.ManagementGroupId);
        var aliasMatch = teams.FirstOrDefault(team => team.ManagementGroupIds.Any(managementGroupId =>
            NormalizeKey(managementGroupId).Equals(parentId, StringComparison.OrdinalIgnoreCase)));
        if (aliasMatch != null)
        {
            return aliasMatch;
        }

        var businessName = ExtractBusinessName(parent.ManagementGroupDisplayName);
        var businessMatches = teams.Where(team =>
                !IsAutomaticTeamForParent(team, parent)
                && (NormalizeKey(team.Id).Equals(businessName, StringComparison.OrdinalIgnoreCase)
                    || NormalizeKey(team.Name).Equals(businessName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (businessMatches.Count == 1)
        {
            AddManagementGroupAlias(businessMatches[0], parent.ManagementGroupId);
            return businessMatches[0];
        }

        return teams.FirstOrDefault(team =>
            NormalizeKey(team.Id).Equals(parentId, StringComparison.OrdinalIgnoreCase)
            || NormalizeKey(team.Name).Equals(NormalizeKey(parent.ManagementGroupDisplayName), StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractBusinessName(string displayName)
    {
        var segments = displayName.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return NormalizeKey(segments.Length > 1 ? segments[^1] : displayName);
    }

    private static bool IsAutomaticTeamForParent(TeamOwnership team, ParentManagementGroupInfo parent)
        => IsAutomaticTeam(team)
            && (NormalizeKey(team.Id).Equals(NormalizeKey(parent.ManagementGroupId), StringComparison.OrdinalIgnoreCase)
                || NormalizeKey(team.Name).Equals(NormalizeKey(parent.ManagementGroupDisplayName), StringComparison.OrdinalIgnoreCase));

    private static bool IsAutomaticTeam(TeamOwnership team)
        => team.Source.Equals("managementGroup", StringComparison.OrdinalIgnoreCase)
            || (team.Subscriptions.Count > 0
                && team.Subscriptions.All(subscription =>
                    subscription.Source.Equals("managementGroup", StringComparison.OrdinalIgnoreCase)));

    private static void AddManagementGroupAlias(TeamOwnership team, string managementGroupId)
    {
        if (!string.IsNullOrWhiteSpace(managementGroupId)
            && !team.ManagementGroupIds.Contains(managementGroupId, StringComparer.OrdinalIgnoreCase))
        {
            team.ManagementGroupIds.Add(managementGroupId.Trim());
        }
    }

    private static void AddConflict(
        OwnershipManagementGroupSyncResult result,
        SubscriptionOwnership subscription,
        TeamOwnership currentTeam,
        TeamOwnership? suggestedTeam,
        ParentManagementGroupInfo parent)
    {
        result.Conflicts++;
        result.ConflictDetails.Add(new OwnershipManagementGroupSyncConflict
        {
            SubscriptionId = subscription.SubscriptionId,
            SubscriptionName = subscription.SubscriptionName,
            CurrentTeamId = currentTeam.Id,
            CurrentTeamName = currentTeam.Name,
            ManagementGroupId = parent.ManagementGroupId,
            ManagementGroupName = parent.ManagementGroupDisplayName,
            SuggestedTeamId = suggestedTeam?.Id ?? string.Empty,
            SuggestedTeamName = suggestedTeam?.Name ?? string.Empty
        });
    }

    private static string NormalizeKey(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static string ExtractSubscriptionName(
        IReadOnlyDictionary<string, object> details,
        string subscriptionId)
    {
        if (!details.TryGetValue(subscriptionId, out var raw) || raw == null)
        {
            return subscriptionId;
        }

        var json = JsonSerializer.Serialize(raw);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("display_name", out var displayName)
            ? displayName.GetString() ?? subscriptionId
            : subscriptionId;
    }
}