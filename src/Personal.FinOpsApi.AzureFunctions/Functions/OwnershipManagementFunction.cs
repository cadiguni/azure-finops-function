using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class OwnershipManagementFunction
{
    private readonly OwnershipStorageService _ownershipStorageService;
    private readonly OwnershipManagementGroupSyncService _managementGroupSyncService;
    private readonly CurrentUserService _currentUserService;
    private readonly AuthorizationService _authorizationService;
    private readonly ILogger<OwnershipManagementFunction> _logger;

    public OwnershipManagementFunction(
        OwnershipStorageService ownershipStorageService,
        OwnershipManagementGroupSyncService managementGroupSyncService,
        CurrentUserService currentUserService,
        AuthorizationService authorizationService,
        ILogger<OwnershipManagementFunction> logger)
    {
        _ownershipStorageService = ownershipStorageService;
        _managementGroupSyncService = managementGroupSyncService;
        _currentUserService = currentUserService;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    [Function("OwnershipListTeams")]
    public async Task<HttpResponseData> ListTeams(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ownership/teams")] HttpRequestData req)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanRead(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão para visualizar ownership.");
        }

        var missingConfigResponse = await EnsureOwnershipConfigExistsAsync(req);
        if (missingConfigResponse != null)
        {
            return missingConfigResponse;
        }

        var config = await _ownershipStorageService.GetConfigAsync();
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            version = config.Version,
            updatedAt = config.UpdatedAt,
            updatedBy = config.UpdatedBy,
            lastManagementGroupSyncAt = config.LastManagementGroupSyncAt,
            lastManagementGroupSyncAdded = config.LastManagementGroupSyncAdded,
            lastManagementGroupSyncCreatedTeams = config.LastManagementGroupSyncCreatedTeams,
            lastManagementGroupSyncConflicts = config.LastManagementGroupSyncConflicts,
            teamsCount = config.Teams.Count,
            teams = config.Teams.Select(t => new
            {
                id = t.Id,
                name = t.Name,
                requiredRole = t.RequiredRole,
                subscriptionsCount = t.Subscriptions.Count,
                subscriptions = t.Subscriptions.Select(s => new
                {
                    subscriptionId = s.SubscriptionId,
                    subscriptionName = s.SubscriptionName,
                    environment = s.Environment,
                    enabled = s.Enabled,
                    source = s.Source,
                    syncedAt = s.SyncedAt
                })
            })
        });
        return response;
    }

    [Function("OwnershipGetTeam")]
    public async Task<HttpResponseData> GetTeam(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ownership/teams/{teamId}")] HttpRequestData req,
        string teamId)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanRead(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão para visualizar ownership.");
        }

        var missingConfigResponse = await EnsureOwnershipConfigExistsAsync(req);
        if (missingConfigResponse != null)
        {
            return missingConfigResponse;
        }

        var team = await _ownershipStorageService.GetTeamAsync(teamId);
        if (team == null)
        {
            return await ApiHttpResponses.NotFound(req, $"Time '{teamId}' não encontrado.");
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            id = team.Id,
            name = team.Name,
            requiredRole = team.RequiredRole,
            subscriptionsCount = team.Subscriptions.Count,
            subscriptions = team.Subscriptions.Select(s => new
            {
                subscriptionId = s.SubscriptionId,
                subscriptionName = s.SubscriptionName,
                environment = s.Environment,
                enabled = s.Enabled
            })
        });
        return response;
    }

    [Function("OwnershipAddSubscription")]
    public async Task<HttpResponseData> AddSubscription(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ownership/teams/{teamId}/subscriptions")] HttpRequestData req,
        string teamId)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanEdit(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão para alterar ownership.");
        }

        var missingConfigResponse = await EnsureOwnershipConfigExistsAsync(req);
        if (missingConfigResponse != null)
        {
            return missingConfigResponse;
        }

        var payload = await ReadPayloadAsync(req);
        if (payload == null)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", "Payload inválido.");
        }

        var validationError = ValidateSubscription(payload);
        if (validationError != null)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", validationError);
        }

        var config = await _ownershipStorageService.GetConfigAsync();
        var team = config.Teams.FirstOrDefault(t => t.Id.Equals(teamId, StringComparison.OrdinalIgnoreCase));
        if (team == null)
        {
            return await ApiHttpResponses.NotFound(req, $"Time '{teamId}' não encontrado.");
        }

        if (team.Subscriptions.Any(s => s.SubscriptionId.Equals(payload.SubscriptionId, StringComparison.OrdinalIgnoreCase)))
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.Conflict, "Conflict", $"Subscription '{payload.SubscriptionId}' já está associada ao time '{team.Id}'.");
        }

        var teamAlreadyAssigned = config.Teams.FirstOrDefault(t =>
            !t.Id.Equals(team.Id, StringComparison.OrdinalIgnoreCase)
            && t.Subscriptions.Any(s => s.SubscriptionId.Equals(payload.SubscriptionId, StringComparison.OrdinalIgnoreCase)));
        if (teamAlreadyAssigned != null)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.Conflict, "Conflict", $"Subscription '{payload.SubscriptionId}' já está associada ao time '{teamAlreadyAssigned.Id}'.");
        }

        payload.Source = "manual";
        payload.SyncedAt = null;
        team.Subscriptions.Add(payload);
        await _ownershipStorageService.SaveConfigAsync(config, ResolveUpdatedBy(user));

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new
        {
            message = "Subscription adicionada com sucesso.",
            teamId = team.Id,
            subscription = new
            {
                subscriptionId = payload.SubscriptionId,
                subscriptionName = payload.SubscriptionName,
                environment = payload.Environment,
                enabled = payload.Enabled
            }
        });
        return response;
    }

    [Function("OwnershipUpdateSubscription")]
    public async Task<HttpResponseData> UpdateSubscription(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "ownership/teams/{teamId}/subscriptions/{subscriptionId}")] HttpRequestData req,
        string teamId,
        string subscriptionId)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanEdit(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão para alterar ownership.");
        }

        var missingConfigResponse = await EnsureOwnershipConfigExistsAsync(req);
        if (missingConfigResponse != null)
        {
            return missingConfigResponse;
        }

        var payload = await ReadUpdatePayloadAsync(req);
        if (payload == null)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", "Payload inválido.");
        }

        var validationError = ValidateSubscription(payload);
        if (validationError != null)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", validationError);
        }

        var config = await _ownershipStorageService.GetConfigAsync();
        var team = config.Teams.FirstOrDefault(t => t.Id.Equals(teamId, StringComparison.OrdinalIgnoreCase));
        if (team == null)
        {
            return await ApiHttpResponses.NotFound(req, $"Time '{teamId}' não encontrado.");
        }

        var current = team.Subscriptions.FirstOrDefault(s => s.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase));
        if (current == null)
        {
            return await ApiHttpResponses.NotFound(req, $"Subscription '{subscriptionId}' não encontrada no time '{teamId}'.");
        }

        var duplicateInTeam = team.Subscriptions.Any(s =>
            !s.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase)
            && s.SubscriptionId.Equals(payload.SubscriptionId, StringComparison.OrdinalIgnoreCase));
        if (duplicateInTeam)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.Conflict, "Conflict", $"Subscription '{payload.SubscriptionId}' já está associada ao time '{team.Id}'.");
        }

        var destinationTeam = team;
        if (!string.IsNullOrWhiteSpace(payload.TargetTeamId)
            && !payload.TargetTeamId.Equals(team.Id, StringComparison.OrdinalIgnoreCase))
        {
            destinationTeam = config.Teams.FirstOrDefault(t => t.Id.Equals(payload.TargetTeamId, StringComparison.OrdinalIgnoreCase));
            if (destinationTeam == null)
            {
                return await ApiHttpResponses.NotFound(req, $"Time de destino '{payload.TargetTeamId}' não encontrado.");
            }
        }

        var duplicateInOtherTeam = config.Teams.Any(t =>
            !t.Id.Equals(team.Id, StringComparison.OrdinalIgnoreCase)
            && t.Subscriptions.Any(s => s.SubscriptionId.Equals(payload.SubscriptionId, StringComparison.OrdinalIgnoreCase))
            && !(
                destinationTeam != null
                && t.Id.Equals(destinationTeam.Id, StringComparison.OrdinalIgnoreCase)
                && current.SubscriptionId.Equals(payload.SubscriptionId, StringComparison.OrdinalIgnoreCase)));
        if (duplicateInOtherTeam)
        {
            return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.Conflict, "Conflict", $"Subscription '{payload.SubscriptionId}' já está associada a outro time.");
        }

        current.SubscriptionId = payload.SubscriptionId.Trim();
        current.SubscriptionName = payload.SubscriptionName.Trim();
        current.Environment = payload.Environment.Trim();
        current.Enabled = payload.Enabled;
        current.Source = "manual";
        current.SyncedAt = null;
        current.ManagementGroupOverride = false;

        if (!destinationTeam.Id.Equals(team.Id, StringComparison.OrdinalIgnoreCase))
        {
            var existingInDestination = destinationTeam.Subscriptions.FirstOrDefault(s =>
                s.SubscriptionId.Equals(payload.SubscriptionId, StringComparison.OrdinalIgnoreCase));

            if (existingInDestination != null)
            {
                return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.Conflict, "Conflict",
                    $"Subscription '{payload.SubscriptionId}' já está associada ao time '{destinationTeam.Id}'.");
            }

            team.Subscriptions.Remove(current);
            destinationTeam.Subscriptions.Add(current);
        }

        await _ownershipStorageService.SaveConfigAsync(config, ResolveUpdatedBy(user));

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            message = "Subscription atualizada com sucesso.",
            teamId = destinationTeam.Id,
            subscription = new
            {
                subscriptionId = current.SubscriptionId,
                subscriptionName = current.SubscriptionName,
                environment = current.Environment,
                enabled = current.Enabled
            }
        });
        return response;
    }

    [Function("OwnershipAcceptManagementGroupOverrides")]
    public async Task<HttpResponseData> AcceptManagementGroupOverrides(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ownership/management-group-overrides")] HttpRequestData req)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanEdit(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão para alterar ownership.");
        }

        var payload = JsonSerializer.Deserialize<AcceptManagementGroupOverridesPayload>(
            await req.ReadAsStringAsync() ?? "{}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var requestedIds = payload?.SubscriptionIds
            .Where(subscriptionId => !string.IsNullOrWhiteSpace(subscriptionId))
            .Select(subscriptionId => subscriptionId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (requestedIds.Count == 0)
        {
            return await ApiHttpResponses.WriteErrorAsync(
                req,
                HttpStatusCode.BadRequest,
                "BadRequest",
                "Informe ao menos uma subscription para manter no time atual.");
        }

        var config = await _ownershipStorageService.GetConfigAsync();
        var accepted = 0;
        foreach (var subscription in config.Teams.SelectMany(team => team.Subscriptions))
        {
            if (requestedIds.Contains(subscription.SubscriptionId))
            {
                subscription.ManagementGroupOverride = true;
                subscription.Source = "manual";
                subscription.SyncedAt = null;
                accepted++;
            }
        }

        if (accepted > 0)
        {
            await _ownershipStorageService.SaveConfigAsync(config, ResolveUpdatedBy(user));
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            message = "Exceções de Management Group registradas.",
            accepted,
            notFound = requestedIds.Count - accepted
        });
        return response;
    }

    [Function("OwnershipSyncFromManagementGroups")]
    public async Task<HttpResponseData> SyncFromManagementGroups(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ownership/sync-management-groups")] HttpRequestData req)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanEdit(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão para alterar ownership.");
        }

        var missingConfigResponse = await EnsureOwnershipConfigExistsAsync(req);
        if (missingConfigResponse != null)
        {
            return missingConfigResponse;
        }

        try
        {
            var result = await _managementGroupSyncService.SyncAsync(ResolveUpdatedBy(user));

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                message = "Sincronização por Parent Management Group concluída.",
                totalSubscriptionsDiscovered = result.TotalSubscriptionsDiscovered,
                totalSubscriptionsWithParent = result.TotalSubscriptionsWithParent,
                added = result.Added,
                createdTeams = result.CreatedTeams,
                movedAutomaticAssignments = result.MovedAutomaticAssignments,
                removedAutomaticTeams = result.RemovedAutomaticTeams,
                skippedAlreadyAssigned = result.SkippedAlreadyAssigned,
                conflicts = result.Conflicts,
                conflictDetails = result.ConflictDetails.Select(conflict => new
                {
                    subscriptionId = conflict.SubscriptionId,
                    subscriptionName = conflict.SubscriptionName,
                    currentTeamId = conflict.CurrentTeamId,
                    currentTeamName = conflict.CurrentTeamName,
                    managementGroupId = conflict.ManagementGroupId,
                    managementGroupName = conflict.ManagementGroupName,
                    suggestedTeamId = conflict.SuggestedTeamId,
                    suggestedTeamName = conflict.SuggestedTeamName
                }),
                skippedWithoutTeamMatch = result.SkippedWithoutTeamMatch,
                skippedWithoutParent = result.SkippedWithoutParent
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OWNERSHIP-SYNC] Erro ao sincronizar subscriptions por parent MG.");
            return await ApiHttpResponses.InternalServerError(req, $"Erro ao sincronizar ownership por parent MG: {ex.Message}");
        }
    }

    [Function("OwnershipDeleteSubscription")]
    public async Task<HttpResponseData> DeleteSubscription(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "ownership/teams/{teamId}/subscriptions/{subscriptionId}")] HttpRequestData req,
        string teamId,
        string subscriptionId)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanEdit(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão para alterar ownership.");
        }

        var missingConfigResponse = await EnsureOwnershipConfigExistsAsync(req);
        if (missingConfigResponse != null)
        {
            return missingConfigResponse;
        }

        var config = await _ownershipStorageService.GetConfigAsync();
        var team = config.Teams.FirstOrDefault(t => t.Id.Equals(teamId, StringComparison.OrdinalIgnoreCase));
        if (team == null)
        {
            return await ApiHttpResponses.NotFound(req, $"Time '{teamId}' não encontrado.");
        }

        var current = team.Subscriptions.FirstOrDefault(s => s.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase));
        if (current == null)
        {
            return await ApiHttpResponses.NotFound(req, $"Subscription '{subscriptionId}' não encontrada no time '{teamId}'.");
        }

        team.Subscriptions.Remove(current);
        await _ownershipStorageService.SaveConfigAsync(config, ResolveUpdatedBy(user));

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            message = "Subscription removida com sucesso.",
            teamId = team.Id,
            subscriptionId
        });
        return response;
    }

    private static async Task<SubscriptionOwnership?> ReadPayloadAsync(HttpRequestData req)
    {
        var body = await req.ReadAsStringAsync();
        return JsonSerializer.Deserialize<SubscriptionOwnership>(
            body ?? "{}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static async Task<UpdateSubscriptionOwnershipPayload?> ReadUpdatePayloadAsync(HttpRequestData req)
    {
        var body = await req.ReadAsStringAsync();
        return JsonSerializer.Deserialize<UpdateSubscriptionOwnershipPayload>(
            body ?? "{}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static string? ValidateSubscription(UpdateSubscriptionOwnershipPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.SubscriptionId))
        {
            return "subscriptionId é obrigatório.";
        }

        if (string.IsNullOrWhiteSpace(payload.SubscriptionName))
        {
            return "subscriptionName é obrigatório.";
        }

        return null;
    }

    private static string? ValidateSubscription(SubscriptionOwnership payload)
    {
        if (string.IsNullOrWhiteSpace(payload.SubscriptionId))
        {
            return "subscriptionId é obrigatório.";
        }

        if (string.IsNullOrWhiteSpace(payload.SubscriptionName))
        {
            return "subscriptionName é obrigatório.";
        }

        return null;
    }

    private static string ResolveUpdatedBy(CurrentUserContext user)
        => !string.IsNullOrWhiteSpace(user.UserPrincipalName)
            ? user.UserPrincipalName
            : (!string.IsNullOrWhiteSpace(user.DisplayName) ? user.DisplayName : "unknown");

    private async Task<HttpResponseData?> EnsureOwnershipConfigExistsAsync(HttpRequestData req)
    {
        if (await _ownershipStorageService.ConfigExistsAsync())
        {
            return null;
        }

        var response = req.CreateResponse(HttpStatusCode.NotFound);
        await response.WriteAsJsonAsync(new
        {
            error = "OwnershipConfigNotFound",
            message = "Arquivo de ownership não encontrado",
            expectedPath = _ownershipStorageService.GetExpectedPath()
        });
        return response;
    }
}

public class UpdateSubscriptionOwnershipPayload : SubscriptionOwnership
{
    public string? TargetTeamId { get; set; }
}

public class AcceptManagementGroupOverridesPayload
{
    public List<string> SubscriptionIds { get; set; } = new();
}
