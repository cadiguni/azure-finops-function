using Microsoft.Azure.Functions.Worker.Http;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Personal.FinOpsApi.AzureFunctions.Services;

namespace Personal.FinOpsApi.AzureFunctions.Functions
{
    /// <summary>
    /// API simplificada para gerenciar mapeamento de times -> subscriptions
    /// </summary>
    public class TeamManagementFunction
    {
        private readonly TeamSubscriptionsService _teamService;
        private readonly ILogger<TeamManagementFunction> _logger;

        public TeamManagementFunction(
            TeamSubscriptionsService teamService,
            ILogger<TeamManagementFunction> logger)
        {
            _teamService = teamService;
            _logger = logger;
        }

        /// <summary>
        /// GET /api/teams - Lista todos os times
        /// </summary>
        [Function("ListTeams")]
        public async Task<HttpResponseData> ListTeams(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "teams")] HttpRequestData req)
        {
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
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteStringAsync("Erro interno ao processar a requisição.");
                return response;
            }
        }

        /// <summary>
        /// GET /api/teams/{teamId} - Obtém um time específico
        /// </summary>
        [Function("GetTeam")]
        public async Task<HttpResponseData> GetTeam(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "teams/{teamId}")] HttpRequestData req,
            string teamId)
        {
            _logger.LogInformation("🔍 Buscando time: {TeamId}", teamId);
            
            try
            {
                var team = await _teamService.GetTeamAsync(teamId);
                
                if (team == null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteStringAsync($"Time '{teamId}' não encontrado");
                    return notFound;
                }
                
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(team);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro ao buscar time {TeamId}", teamId);
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteStringAsync("Erro interno ao processar a requisição.");
                return response;
            }
        }

        /// <summary>
        /// POST /api/teams - Cria ou atualiza um time
        /// Body: { "id": "plataforma", "name": "Plataforma", "email": "...", "subscriptionIds": ["..."] }
        /// </summary>
        [Function("UpsertTeam")]
        public async Task<HttpResponseData> UpsertTeam(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "teams")] HttpRequestData req)
        {
            _logger.LogInformation("➕ Criando/atualizando time");
            
            try
            {
                var body = await req.ReadAsStringAsync();
                var team = JsonSerializer.Deserialize<TeamConfig>(body ?? "{}", 
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (team == null || string.IsNullOrEmpty(team.Id))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteStringAsync("ID do time é obrigatório");
                    return badRequest;
                }
                
                var saved = await _teamService.UpsertTeamAsync(team);
                
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = $"Time '{saved.Id}' salvo com sucesso",
                    team = saved
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro ao criar/atualizar time");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteStringAsync("Erro interno ao processar a requisição.");
                return response;
            }
        }

        /// <summary>
        /// DELETE /api/teams/{teamId} - Remove um time
        /// </summary>
        [Function("DeleteTeam")]
        public async Task<HttpResponseData> DeleteTeam(
            [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "teams/{teamId}")] HttpRequestData req,
            string teamId)
        {
            _logger.LogInformation("🗑️ Removendo time: {TeamId}", teamId);
            
            try
            {
                var deleted = await _teamService.DeleteTeamAsync(teamId);
                
                if (!deleted)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteStringAsync($"Time '{teamId}' não encontrado");
                    return notFound;
                }
                
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = $"Time '{teamId}' removido com sucesso"
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro ao remover time {TeamId}", teamId);
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteStringAsync("Erro interno ao processar a requisição.");
                return response;
            }
        }

        /// <summary>
        /// GET /api/teams/subscriptions - Lista todas as subscriptions e seus times
        /// </summary>
        [Function("ListTeamSubscriptions")]
        public async Task<HttpResponseData> ListTeamSubscriptions(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "teams/subscriptions")] HttpRequestData req)
        {
            _logger.LogInformation("📋 Listando mapeamento subscriptions -> times");
            
            try
            {
                var mappings = await _teamService.GetAllSubscriptionTeamMappingsAsync();
                
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    mappingsCount = mappings.Count,
                    subscriptions = mappings.Select(kvp => new
                    {
                        subscriptionId = kvp.Key,
                        teamId = kvp.Value
                    })
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro ao listar mapeamentos");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteStringAsync("Erro interno ao processar a requisição.");
                return response;
            }
        }
    }
}
