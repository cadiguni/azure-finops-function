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
    private const int PreGeneratedReportFallbackDays = 7;

    private readonly IRecommendationReportService _reportService;
    private readonly HtmlReportBuilder _htmlBuilder;
    private readonly CsvReportBuilder _csvBuilder;
    private readonly TeamSubscriptionsService _teamService;
    private readonly PreGeneratedReportStorageService _preGeneratedReportStorageService;
    private readonly CurrentUserService _currentUserService;
    private readonly AuthorizationService _authorizationService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReportFunction> _logger;

    public ReportFunction(
        IRecommendationReportService reportService,
        HtmlReportBuilder htmlBuilder,
        CsvReportBuilder csvBuilder,
        TeamSubscriptionsService teamService,
        PreGeneratedReportStorageService preGeneratedReportStorageService,
        CurrentUserService currentUserService,
        AuthorizationService authorizationService,
        IConfiguration configuration,
        ILogger<ReportFunction> logger)
    {
        _reportService = reportService;
        _htmlBuilder = htmlBuilder;
        _csvBuilder = csvBuilder;
        _teamService = teamService;
        _preGeneratedReportStorageService = preGeneratedReportStorageService;
        _currentUserService = currentUserService;
        _authorizationService = authorizationService;
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
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanRead(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão de leitura.");
        }

        _logger.LogInformation("🎨 Solicitação de relatório HTML versionado recebida");

        try
        {
            var (date, mgFilter, subFilter, teamFilter) = ParseQueryParams(req);

            var reportData = !string.IsNullOrEmpty(teamFilter)
                ? await _reportService.GenerateReportByTeamAsync(date, teamFilter)
                : await _reportService.GenerateReportAsync(date, mgFilter, subFilter);
            var resolvedDate = reportData.AnalysisDate;
            var htmlContent = _htmlBuilder.BuildReport(reportData, teamFilter);
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/html; charset=utf-8");
            response.Headers.Add("Content-Disposition", 
                $"inline; filename=\"finops-report-{resolvedDate:yyyy-MM-dd}.html\"");
            response.Headers.Add("X-FinOps-Snapshot-Revision", reportData.SnapshotRevision ?? "legacy");
            response.Headers.Add("X-FinOps-Requested-Date", date.ToString("yyyy-MM-dd"));
            response.Headers.Add("X-FinOps-Resolved-Date", resolvedDate.ToString("yyyy-MM-dd"));

            await response.WriteStringAsync(htmlContent);

            _logger.LogInformation("✅ Relatório HTML versionado retornado: revision={revision}, {htmlSize} bytes",
                reportData.SnapshotRevision, htmlContent.Length);

            return response;
        }
        catch (FileNotFoundException ex)
        {
            return await ApiHttpResponses.NotFound(req, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "BadRequest", message = ex.Message });
            return bad;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao gerar relatório HTML");
            return await ApiHttpResponses.InternalServerError(req, "Falha ao gerar relatório HTML.");
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
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        if (!_authorizationService.CanRead(user))
        {
            return await ApiHttpResponses.Forbidden(req, "Usuário sem permissão de leitura.");
        }

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
                $"attachment; filename=\"finops-recommendations-{reportData.AnalysisDate:yyyy-MM-dd}.csv\"");

            response.Headers.Add("X-FinOps-Requested-Date", date.ToString("yyyy-MM-dd"));
            response.Headers.Add("X-FinOps-Resolved-Date", reportData.AnalysisDate.ToString("yyyy-MM-dd"));
            response.Headers.Add("X-FinOps-Snapshot-Revision", reportData.SnapshotRevision ?? "legacy");
            await response.WriteStringAsync(csvContent);

            _logger.LogInformation("✅ Relatório CSV gerado: {recCount} recomendações, {csvSize} bytes", 
                reportData.Summary.TotalRecommendations, csvContent.Length);

            return response;
        }
        catch (FileNotFoundException ex)
        {
            return await ApiHttpResponses.NotFound(req, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "BadRequest", message = ex.Message });
            return bad;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao gerar relatório CSV");
            return await ApiHttpResponses.InternalServerError(req, "Falha ao gerar relatório CSV.");
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

        if (new[] { mgFilter, subFilter, teamFilter }.Count(f => f != null) > 1)
            throw new ArgumentException("Selecione apenas um filtro: time ou assinatura.");
        return (date, mgFilter, subFilter, teamFilter);
    }

}
