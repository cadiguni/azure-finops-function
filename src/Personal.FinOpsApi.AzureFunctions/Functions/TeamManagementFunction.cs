using Microsoft.Azure.Functions.Worker.Http;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Services;

namespace Personal.FinOpsApi.AzureFunctions.Functions
{
    /// <summary>
    /// API de leitura do mapeamento de times -> subscriptions
    /// </summary>
    public class TeamManagementFunction
    {
        private readonly TeamSubscriptionsService _teamService;
        private readonly CurrentUserService _currentUserService;
        private readonly AuthorizationService _authorizationService;
        private readonly ILogger<TeamManagementFunction> _logger;

        public TeamManagementFunction(
            TeamSubscriptionsService teamService,
            CurrentUserService currentUserService,
            AuthorizationService authorizationService,
            ILogger<TeamManagementFunction> logger)
        {
            _teamService = teamService;
            _currentUserService = currentUserService;
            _authorizationService = authorizationService;
            _logger = logger;
        }

        /// <summary>
        /// GET /api/teams - Lista todos os times
        /// </summary>
        [Function("ListTeams")]
        public async Task<HttpResponseData> ListTeams(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "teams")] HttpRequestData req)
        {
            var user = await _currentUserService.GetCurrentUserAsync(req);
            if (!user.IsAuthenticated)
            {
                return await ApiHttpResponses.Unauthorized(req);
            }

            if (!_authorizationService.CanRead(user))
            {
                return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão de leitura.");
            }

            _logger.LogInformation("📋 Listando times configurados");
            
            try
            {
                var config = await _teamService.GetConfigAsync();
                
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    teamsCount = config.Teams.Count,
                    lastUpdated = config.LastUpdated,
                    teams = config.Teams.Select(t => new
                    {
                        id = t.Id,
                        name = t.Name,
                        email = t.Email,
                        subscriptionsCount = t.SubscriptionIds.Count,
                        subscriptionIds = t.SubscriptionIds,
                        subscriptionNames = t.SubscriptionNames
                    })
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro ao listar times");
                return await ApiHttpResponses.InternalServerError(req);
            }
        }

    }
}
