using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// Endpoint manual para forçar a geração de relatórios HTML pré-gerados.
/// Executa a mesma lógica do PreGeneratedReportTimerFunction, mas sob demanda.
/// 
/// POST /api/generate-reports?date=2026-05-22
/// - date: opcional, padrão = hoje UTC
/// 
/// Retorna JSON com status de cada relatório gerado.
/// </summary>
public class ManualReportGenerationFunction
{
    private readonly IRecommendationReportService _reportService;
    private readonly HtmlReportBuilder _htmlBuilder;
    private readonly AnalysisStorageService _analysisStorageService;
    private readonly TeamSubscriptionsService _teamService;
    private readonly CostAnomalyStorageService _anomalyStorageService;
    private readonly PreGeneratedReportStorageService _reportStorageService;
    private readonly ILogger<ManualReportGenerationFunction> _logger;

    public ManualReportGenerationFunction(
        IRecommendationReportService reportService,
        HtmlReportBuilder htmlBuilder,
        AnalysisStorageService analysisStorageService,
        TeamSubscriptionsService teamService,
        CostAnomalyStorageService anomalyStorageService,
        PreGeneratedReportStorageService reportStorageService,
        ILogger<ManualReportGenerationFunction> logger)
    {
        _reportService = reportService;
        _htmlBuilder = htmlBuilder;
        _analysisStorageService = analysisStorageService;
        _teamService = teamService;
        _anomalyStorageService = anomalyStorageService;
        _reportStorageService = reportStorageService;
        _logger = logger;
    }

    [Function("ManualReportGeneration")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "generate-reports")] HttpRequestData req)
    {
        var startTime = DateTime.UtcNow;

        // Parsear data do query string
        var dateParam = req.Query["date"];
        DateTime reportDate;
        if (!string.IsNullOrEmpty(dateParam))
        {
            if (!DateTime.TryParseExact(dateParam, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out reportDate))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync($"Formato de data inválido: '{dateParam}'. Use yyyy-MM-dd.");
                return badResponse;
            }
        }
        else
        {
            reportDate = DateTime.UtcNow.Date;
        }

        _logger.LogInformation("📋 [MANUAL] Iniciando geração manual de relatórios para {date}", reportDate.ToString("yyyy-MM-dd"));

        var results = new List<object>();
        var generated = 0;
        var failed = 0;

        try
        {
            // 1. Verificar dados de análise disponíveis
            var subscriptions = await _analysisStorageService.ListSubscriptionsByDateAsync(reportDate);
            _logger.LogInformation("📋 [MANUAL] Encontradas {count} subscriptions com dados para {date}", 
                subscriptions.Count, reportDate.ToString("yyyy-MM-dd"));

            if (subscriptions.Count == 0)
            {
                var noDataResponse = req.CreateResponse(HttpStatusCode.OK);
                noDataResponse.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await noDataResponse.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    status = "no_data",
                    date = reportDate.ToString("yyyy-MM-dd"),
                    message = $"Nenhum dado de análise encontrado para {reportDate:yyyy-MM-dd}. " +
                              "Execute POST /api/ManualCostAnalysis primeiro para gerar os dados de análise.",
                    blobPrefix = $"analyses/year={reportDate:yyyy}/month={reportDate:MM}/day={reportDate:dd}/",
                    subscriptionsFound = 0
                }, new JsonSerializerOptions { WriteIndented = true }));
                return noDataResponse;
            }

            // 2. Carregar anomalias (opcional)
            CostAnomalyReport? anomalyReport = null;
            try
            {
                anomalyReport = await _anomalyStorageService.LoadAnomalyReportAsync(reportDate.ToString("yyyy-MM-dd"));
                _logger.LogInformation("📋 [MANUAL] Anomalias carregadas para {date}", reportDate.ToString("yyyy-MM-dd"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "📋 [MANUAL] Anomalias não disponíveis para {date} - continuando sem", reportDate.ToString("yyyy-MM-dd"));
            }

            // 3. Gerar relatório geral
            try
            {
                var report = await _reportService.GenerateReportAsync(reportDate);
                _logger.LogInformation("📋 [MANUAL] ReportService retornou {count} recomendações, {mgCount} management groups",
                    report.Summary.TotalRecommendations, report.ManagementGroups.Count);

                var html = _htmlBuilder.BuildReport(report, anomalyReport: anomalyReport);
                var path = PreGeneratedReportStorageService.BuildGeneralPath(reportDate);
                await _reportStorageService.SaveHtmlAsync(path, html);

                results.Add(new { type = "general", status = "success", path, htmlSize = html.Length, recommendations = report.Summary.TotalRecommendations });
                generated++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "📋 [MANUAL] Falha ao gerar relatório geral");
                results.Add(new { type = "general", status = "error", message = ex.Message });
                failed++;
            }

            // 4. Gerar relatórios por subscription
            foreach (var subscriptionId in subscriptions)
            {
                try
                {
                    var report = await _reportService.GenerateReportAsync(reportDate, subscriptionFilter: subscriptionId);
                    var html = _htmlBuilder.BuildReport(report, anomalyReport: anomalyReport);
                    var path = PreGeneratedReportStorageService.BuildSubscriptionPath(reportDate, subscriptionId);
                    await _reportStorageService.SaveHtmlAsync(path, html);

                    results.Add(new { type = "subscription", subscriptionId, status = "success", path, recommendations = report.Summary.TotalRecommendations });
                    generated++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "📋 [MANUAL] Falha ao gerar relatório da subscription {sub}", subscriptionId);
                    results.Add(new { type = "subscription", subscriptionId, status = "error", message = ex.Message });
                    failed++;
                }
            }

            // 5. Gerar relatórios por team
            try
            {
                var teamConfig = await _teamService.GetConfigAsync();
                foreach (var team in teamConfig.Teams)
                {
                    if (string.IsNullOrWhiteSpace(team.Id)) continue;

                    try
                    {
                        var report = await _reportService.GenerateReportByTeamAsync(reportDate, team.Id);
                        var teamName = string.IsNullOrWhiteSpace(team.Name) ? team.Id : team.Name;
                        var html = _htmlBuilder.BuildReport(report, teamName, anomalyReport);
                        var path = PreGeneratedReportStorageService.BuildTeamPath(reportDate, team.Id);
                        await _reportStorageService.SaveHtmlAsync(path, html);

                        results.Add(new { type = "team", teamId = team.Id, teamName, status = "success", path, recommendations = report.Summary.TotalRecommendations });
                        generated++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "📋 [MANUAL] Falha ao gerar relatório do time {team}", team.Id);
                        results.Add(new { type = "team", teamId = team.Id, status = "error", message = ex.Message });
                        failed++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "📋 [MANUAL] Erro ao carregar teams - relatórios por time não gerados");
            }

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation("📋 [MANUAL] Geração concluída: {generated} OK, {failed} falhas, {durationMs}ms",
                generated, failed, duration.TotalMilliseconds);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync(JsonSerializer.Serialize(new
            {
                status = "completed",
                date = reportDate.ToString("yyyy-MM-dd"),
                generated,
                failed,
                durationMs = (int)duration.TotalMilliseconds,
                subscriptionsFound = subscriptions.Count,
                subscriptions,
                reports = results,
                reportUrls = new
                {
                    general = $"/api/report/html?date={reportDate:yyyy-MM-dd}",
                    csv = $"/api/report/csv?date={reportDate:yyyy-MM-dd}"
                }
            }, new JsonSerializerOptions { WriteIndented = true }));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "📋 [MANUAL] Erro geral na geração manual de relatórios");

            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            errorResponse.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new
            {
                status = "error",
                date = reportDate.ToString("yyyy-MM-dd"),
                message = ex.Message,
                generated,
                failed
            }, new JsonSerializerOptions { WriteIndented = true }));
            return errorResponse;
        }
    }
}
