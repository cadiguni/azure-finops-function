using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Gvdasa.FinOpsApi.AzureFunctions.Analyzers;
using System.Text.Json;
using System.Net;

namespace Gvdasa.FinOpsApi.AzureFunctions.Functions
{
    public class DuplicateResourcesFunction
    {
        private readonly ILogger<DuplicateResourcesFunction> _logger;
        private readonly DuplicateResourceAnalyzer _analyzer;

        public DuplicateResourcesFunction(
            ILogger<DuplicateResourcesFunction> logger,
            DuplicateResourceAnalyzer analyzer)
        {
            _logger = logger;
            _analyzer = analyzer;
        }

        [Function("AnalyzeDuplicateResources")]
        public async Task<HttpResponseData> AnalyzeDuplicateResources(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            _logger.LogInformation("🔍 Iniciando análise de recursos duplicados em múltiplas assinaturas");

            try
            {
                // Ler lista de assinaturas do corpo da requisição
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonSerializer.Deserialize<AnalyzeRequest>(requestBody, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (request?.SubscriptionIds == null || !request.SubscriptionIds.Any())
                {
                    return await CreateErrorResponse(req, "Lista de assinaturas é obrigatória", HttpStatusCode.BadRequest);
                }

                _logger.LogInformation("📊 Analisando {Count} assinaturas: {Subscriptions}", 
                    request.SubscriptionIds.Count, string.Join(", ", request.SubscriptionIds));

                // Executar análise
                var duplicateGroups = await _analyzer.AnalyzeDuplicatesAcrossSubscriptionsAsync(request.SubscriptionIds);

                // Calcular resumo
                var summary = new AnalysisResult
                {
                    TotalDuplicateGroups = duplicateGroups.Count,
                    TotalDuplicateResources = duplicateGroups.Sum(g => g.Count),
                    TotalPotentialSavings = duplicateGroups.Sum(g => g.PotentialSavings),
                    AnalysisDate = DateTime.UtcNow,
                    SubscriptionsAnalyzed = request.SubscriptionIds,
                    DuplicateGroups = duplicateGroups.Take(50).ToList(), // Limitar para evitar payloads muito grandes
                    TopSavingsOpportunities = duplicateGroups
                        .OrderByDescending(g => g.PotentialSavings)
                        .Take(10)
                        .Select(g => new SavingsOpportunity
                        {
                            ResourceName = g.Name,
                            ResourceType = g.ResourceType.ToString(),
                            Count = g.Count,
                            PotentialSavings = g.PotentialSavings,
                            Subscriptions = g.GetSubscriptions(),
                            Locations = g.GetLocations()
                        })
                        .ToList()
                };

                _logger.LogInformation("✅ Análise concluída: {Groups} grupos, {Resources} recursos, ${Savings:F2} economia potencial", 
                    summary.TotalDuplicateGroups, 
                    summary.TotalDuplicateResources, 
                    summary.TotalPotentialSavings);

                var response = req.CreateResponse(HttpStatusCode.OK);
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await response.WriteStringAsync(JsonSerializer.Serialize(summary, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }));

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro durante análise de recursos duplicados");
                return await CreateErrorResponse(req, "Erro interno do servidor", HttpStatusCode.InternalServerError);
            }
        }

        private async Task<HttpResponseData> CreateErrorResponse(HttpRequestData req, string message, HttpStatusCode statusCode)
        {
            var response = req.CreateResponse(statusCode);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync(JsonSerializer.Serialize(new { error = message }));
            return response;
        }
    }

    public class AnalyzeRequest
    {
        public List<string> SubscriptionIds { get; set; } = new();
    }

    public class AnalysisResult
    {
        public int TotalDuplicateGroups { get; set; }
        public int TotalDuplicateResources { get; set; }
        public decimal TotalPotentialSavings { get; set; }
        public DateTime AnalysisDate { get; set; }
        public List<string> SubscriptionsAnalyzed { get; set; } = new();
        public List<DuplicateResourceGroup> DuplicateGroups { get; set; } = new();
        public List<SavingsOpportunity> TopSavingsOpportunities { get; set; } = new();
    }

    public class SavingsOpportunity
    {
        public string ResourceName { get; set; } = string.Empty;
        public string ResourceType { get; set; } = string.Empty;
        public int Count { get; set; }
        public decimal PotentialSavings { get; set; }
        public List<string> Subscriptions { get; set; } = new();
        public List<string> Locations { get; set; } = new();
    }
}