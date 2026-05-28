using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// Function unificada para geração de relatórios operacionais em HTML e CSV
/// </summary>
public class ReportFunction
{
    private readonly IRecommendationReportService _reportService;
    private readonly HtmlReportBuilder _htmlBuilder;
    private readonly CsvReportBuilder _csvBuilder;
    private readonly TeamSubscriptionsService _teamService;
    private readonly CostAnomalyStorageService _anomalyStorageService;
    private readonly PreGeneratedReportStorageService _preGeneratedReportStorageService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReportFunction> _logger;

    public ReportFunction(
        IRecommendationReportService reportService,
        HtmlReportBuilder htmlBuilder,
        CsvReportBuilder csvBuilder,
        TeamSubscriptionsService teamService,
        CostAnomalyStorageService anomalyStorageService,
        PreGeneratedReportStorageService preGeneratedReportStorageService,
        IConfiguration configuration,
        ILogger<ReportFunction> logger)
    {
        _reportService = reportService;
        _htmlBuilder = htmlBuilder;
        _csvBuilder = csvBuilder;
        _teamService = teamService;
        _anomalyStorageService = anomalyStorageService;
        _preGeneratedReportStorageService = preGeneratedReportStorageService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Gera relatório em formato HTML
    /// GET /api/report/html?date=YYYY-MM-DD&managementGroup=xxx&subscription=yyy&team=zzz
    /// </summary>
    [Function("GenerateHtmlReport")]
    public async Task<HttpResponseData> GenerateHtmlReportAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "report/html")] HttpRequestData req)
    {
        _logger.LogInformation("🎨 Solicitação de relatório HTML pré-gerado recebida");

        try
        {
            var (date, mgFilter, subFilter, teamFilter) = ParseQueryParams(req);

            if (!string.IsNullOrEmpty(mgFilter))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync(
                    "Relatórios HTML pré-gerados não suportam filtro managementGroup. Use filtros por subscription, team ou relatório geral.");
                return badResponse;
            }

            var blobPath = ResolvePreGeneratedHtmlPath(date, subFilter, teamFilter);
            var htmlContent = await _preGeneratedReportStorageService.LoadHtmlAsync(blobPath);

            if (htmlContent == null)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                notFoundResponse.Headers.Add("Content-Type", "text/html; charset=utf-8");
                await notFoundResponse.WriteStringAsync(
                    $"<html><body><h1>Relatório não encontrado</h1><p>Arquivo pré-gerado não existe: {WebUtility.HtmlEncode(blobPath)}</p></body></html>");
                return notFoundResponse;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/html; charset=utf-8");
            response.Headers.Add("Content-Disposition", 
                $"inline; filename=\"finops-report-{date:yyyy-MM-dd}.html\"");

            await response.WriteStringAsync(htmlContent);

            _logger.LogInformation("✅ Relatório HTML pré-gerado retornado: {path}, {htmlSize} bytes", 
                blobPath, htmlContent.Length);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao gerar relatório HTML");
            
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync(
                $"<html><body><h1>Erro</h1><p>Falha ao gerar relatório: {ex.Message}</p></body></html>");
            
            return errorResponse;
        }
    }

    /// <summary>
    /// Gera relatório em formato CSV
    /// GET /api/report/csv?date=YYYY-MM-DD&managementGroup=xxx&subscription=yyy&team=zzz
    /// </summary>
    [Function("GenerateCsvReport")]
    public async Task<HttpResponseData> GenerateCsvReportAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "report/csv")] HttpRequestData req)
    {
        _logger.LogInformation("📄 Solicitação de relatório CSV recebida");

        try
        {
            var (date, mgFilter, subFilter, teamFilter) = ParseQueryParams(req);
            
            // Gerar dados do relatório - usar team se especificado, senão usar filtros tradicionais
            var reportData = !string.IsNullOrEmpty(teamFilter)
                ? await _reportService.GenerateReportByTeamAsync(date, teamFilter)
                : await _reportService.GenerateReportAsync(date, mgFilter, subFilter);
            
            // Converter para CSV
            var csvContent = _csvBuilder.BuildReport(reportData);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/csv; charset=utf-8");
            response.Headers.Add("Content-Disposition", 
                $"attachment; filename=\"finops-recommendations-{date:yyyy-MM-dd}.csv\"");

            await response.WriteStringAsync(csvContent);

            _logger.LogInformation("✅ Relatório CSV gerado: {recCount} recomendações, {csvSize} bytes", 
                reportData.Summary.TotalRecommendations, csvContent.Length);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao gerar relatório CSV");
            
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Error,Message\nGeneration Failed,{ex.Message}");
            
            return errorResponse;
        }
    }

    /// <summary>
    /// Extrai parâmetros de query da requisição
    /// </summary>
    private (DateTime date, string? mgFilter, string? subFilter, string? teamFilter) ParseQueryParams(HttpRequestData req)
    {
        // Data da análise (padrão: hoje - para análises manuais; use ontem para pipelines diárias)
        var dateParam = req.Query["date"];
        var date = string.IsNullOrEmpty(dateParam) 
            ? DateTime.UtcNow.Date
            : DateTime.ParseExact(dateParam, "yyyy-MM-dd", null);

        // Filtros opcionais
        var mgFilter = req.Query["managementGroup"];
        var subFilter = req.Query["subscription"];
        var teamFilter = req.Query["team"];

        // Limpar filtros vazios
        if (string.IsNullOrWhiteSpace(mgFilter)) mgFilter = null;
        if (string.IsNullOrWhiteSpace(subFilter)) subFilter = null;
        if (string.IsNullOrWhiteSpace(teamFilter)) teamFilter = null;

        _logger.LogInformation("📊 Parâmetros parseados: date={date}, mgFilter={mgFilter}, subFilter={subFilter}, teamFilter={teamFilter}", 
            date.ToString("yyyy-MM-dd"), mgFilter ?? "all", subFilter ?? "all", teamFilter ?? "all");

        return (date, mgFilter, subFilter, teamFilter);
    }

    private static string ResolvePreGeneratedHtmlPath(DateTime date, string? subFilter, string? teamFilter)
    {
        if (!string.IsNullOrEmpty(teamFilter))
        {
            return PreGeneratedReportStorageService.BuildTeamPath(date, teamFilter);
        }

        if (!string.IsNullOrEmpty(subFilter))
        {
            return PreGeneratedReportStorageService.BuildSubscriptionPath(date, subFilter);
        }

        return PreGeneratedReportStorageService.BuildGeneralPath(date);
    }
}
