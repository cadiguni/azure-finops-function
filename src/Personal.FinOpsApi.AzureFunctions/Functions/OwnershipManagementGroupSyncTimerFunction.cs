using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public sealed class OwnershipManagementGroupSyncTimerFunction
{
    private readonly OwnershipManagementGroupSyncService _syncService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OwnershipManagementGroupSyncTimerFunction> _logger;

    public OwnershipManagementGroupSyncTimerFunction(
        OwnershipManagementGroupSyncService syncService,
        IConfiguration configuration,
        ILogger<OwnershipManagementGroupSyncTimerFunction> logger)
    {
        _syncService = syncService;
        _configuration = configuration;
        _logger = logger;
    }

    [Function("OwnershipManagementGroupSyncTimer")]
    public async Task RunAsync(
        [TimerTrigger("%OwnershipManagementGroupSyncSchedule%")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                _configuration["EnableOwnershipManagementGroupSync"],
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[OWNERSHIP-SYNC-TIMER] Sincronização automática desabilitada.");
            return;
        }

        _logger.LogInformation("[OWNERSHIP-SYNC-TIMER] Sincronização automática iniciada.");
        var result = await _syncService.SyncAsync("management-group-sync-timer", cancellationToken);
        _logger.LogInformation(
            "[OWNERSHIP-SYNC-TIMER] Concluída. discovered={discovered} added={added} createdTeams={createdTeams} moved={moved} removedTeams={removedTeams} alreadyAssigned={alreadyAssigned} conflicts={conflicts} withoutParent={withoutParent}",
            result.TotalSubscriptionsDiscovered,
            result.Added,
            result.CreatedTeams,
            result.MovedAutomaticAssignments,
            result.RemovedAutomaticTeams,
            result.SkippedAlreadyAssigned,
            result.Conflicts,
            result.SkippedWithoutParent);
    }
}