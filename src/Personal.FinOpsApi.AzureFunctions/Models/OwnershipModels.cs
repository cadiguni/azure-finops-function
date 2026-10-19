namespace Personal.FinOpsApi.AzureFunctions.Models;

public class OwnershipConfig
{
    public int Version { get; set; } = 1;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = "system";
    public DateTime? LastManagementGroupSyncAt { get; set; }
    public int LastManagementGroupSyncAdded { get; set; }
    public int LastManagementGroupSyncCreatedTeams { get; set; }
    public int LastManagementGroupSyncConflicts { get; set; }
    public List<TeamOwnership> Teams { get; set; } = new();
}

public class TeamOwnership
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RequiredRole { get; set; } = string.Empty;
    public string Source { get; set; } = "manual";
    public List<string> ManagementGroupIds { get; set; } = new();
    public List<SubscriptionOwnership> Subscriptions { get; set; } = new();
}

public class SubscriptionOwnership
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Source { get; set; } = "manual";
    public DateTime? SyncedAt { get; set; }
    public bool ManagementGroupOverride { get; set; }
}

public class OwnershipManagementGroupSyncResult
{
    public int TotalSubscriptionsDiscovered { get; set; }
    public int TotalSubscriptionsWithParent { get; set; }
    public int Added { get; set; }
    public int CreatedTeams { get; set; }
    public int MovedAutomaticAssignments { get; set; }
    public int RemovedAutomaticTeams { get; set; }
    public int SkippedAlreadyAssigned { get; set; }
    public int Conflicts { get; set; }
    public int SkippedWithoutTeamMatch { get; set; }
    public int SkippedWithoutParent { get; set; }
    public List<OwnershipManagementGroupSyncConflict> ConflictDetails { get; set; } = new();
}

public class OwnershipManagementGroupSyncConflict
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;
    public string CurrentTeamId { get; set; } = string.Empty;
    public string CurrentTeamName { get; set; } = string.Empty;
    public string ManagementGroupId { get; set; } = string.Empty;
    public string ManagementGroupName { get; set; } = string.Empty;
    public string SuggestedTeamId { get; set; } = string.Empty;
    public string SuggestedTeamName { get; set; } = string.Empty;
}

public class CurrentUserContext
{
    public bool IsAuthenticated { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string UserPrincipalName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public HashSet<string> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasRole(string role) => Roles.Contains(role);
}
