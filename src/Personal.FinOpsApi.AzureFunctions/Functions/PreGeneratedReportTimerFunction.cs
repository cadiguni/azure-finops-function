using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public class PreGeneratedReportTimerFunction
{
    private readonly IRecommendationReportService _reportService;
    private readonly HtmlReportBuilder _htmlBuilder;
    private readonly AnalysisStorageService _analysisStorageService;
    private readonly TeamSubscriptionsService _teamService;
    private readonly CostAnomalyStorageService _anomalyStorageService;
    private readonly PreGeneratedReportStorageService _reportStorageService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PreGeneratedReportTimerFunction> _logger;

    public PreGeneratedReportTimerFunction(
        IRecommendationReportService reportService,
        HtmlReportBuilder htmlBuilder,
        AnalysisStorageService analysisStorageService,
        TeamSubscriptionsService teamService,
        CostAnomalyStorageService anomalyStorageService,
        PreGeneratedReportStorageService reportStorageService,
        IConfiguration configuration,
        ILogger<PreGeneratedReportTimerFunction> logger)
    {
        _reportService = reportService;
        _htmlBuilder = htmlBuilder;
        _analysisStorageService = analysisStorageService;
        _teamService = teamService;
        _anomalyStorageService = anomalyStorageService;
        _reportStorageService = reportStorageService;
        _configuration = configuration;
        _logger = logger;
    }

    [Function("PreGeneratedReportTimer")]
    public async Task RunAsync([TimerTrigger("%ReportGenerationSchedule%")] TimerInfo timer)
    {
        var reportDate = ResolveReportDate();
        var startTime = DateTime.UtcNow;

        _logger.LogInformation("Iniciando pré-geração de relatórios HTML para {date}", reportDate.ToString("yyyy-MM-dd"));

        // Verificar se existem dados de análise antes de gerar relatórios vazios
        var subscriptions = await _analysisStorageService.ListSubscriptionsByDateAsync(reportDate);
        if (subscriptions.Count == 0)
        {
            _logger.LogWarning(
                "Nenhum dado de análise encontrado para {date}. " +
                "Verifique se o CostAnalysisTimer completou antes do ReportGenerationTimer. " +
                "Use POST /api/generate-reports para gerar manualmente após a análise concluir.",
                reportDate.ToString("yyyy-MM-dd"));
            return;
        }

        _logger.LogInformation("Encontradas {count} subscriptions com dados de análise para {date}",
            subscriptions.Count, reportDate.ToString("yyyy-MM-dd"));

        var anomalyReport = await LoadAnomalyReportAsync(reportDate);
        var generated = 0;
        var failed = 0;

        try
        {
            if (await GenerateGeneralReportAsync(reportDate, anomalyReport))
            {
                generated++;
            }
            else
            {
                failed++;
            }

            foreach (var subscriptionId in subscriptions)
            {
                if (await GenerateSubscriptionReportAsync(reportDate, subscriptionId, anomalyReport))
                {
                    generated++;
                }
                else
                {
                    failed++;
                }
            }

            var teamConfig = await _teamService.GetConfigAsync();
            foreach (var team in teamConfig.Teams)
            {
                if (await GenerateTeamReportAsync(reportDate, team, anomalyReport))
                {
                    generated++;
                }
                else
                {
                    failed++;
                }
            }

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation(
                "Pré-geração de relatórios concluída para {date}. Gerados={generated}, Falhas={failed}, Duração={durationMs}ms",
                reportDate.ToString("yyyy-MM-dd"),
                generated,
                failed,
                duration.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro geral na pré-geração de relatórios HTML para {date}", reportDate.ToString("yyyy-MM-dd"));
            throw;
        }
    }

    private DateTime ResolveReportDate()
    {
        var offsetDays = _configuration.GetValue("ReportGenerationDateOffsetDays", 0);
        return DateTime.UtcNow.Date.AddDays(offsetDays);
    }

    private async Task<CostAnomalyReport?> LoadAnomalyReportAsync(DateTime reportDate)
    {
        var anomalyEnabled = _configuration["EnableCostAnomalyAnalysis"];
        if (string.Equals(anomalyEnabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return await _anomalyStorageService.LoadAnomalyReportAsync(reportDate.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível carregar anomalias de custo para relatórios pré-gerados");
            return null;
        }
    }

    private async Task<bool> GenerateGeneralReportAsync(DateTime reportDate, CostAnomalyReport? anomalyReport)
    {
        try
        {
            var report = await _reportService.GenerateReportAsync(reportDate);
            var html = _htmlBuilder.BuildReport(report, anomalyReport: anomalyReport);
            await _reportStorageService.SaveHtmlAsync(PreGeneratedReportStorageService.BuildGeneralPath(reportDate), html);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao gerar relatório geral para {date}", reportDate.ToString("yyyy-MM-dd"));
            return false;
        }
    }

    private async Task<bool> GenerateSubscriptionReportAsync(DateTime reportDate, string subscriptionId, CostAnomalyReport? anomalyReport)
    {
        try
        {
            var report = await _reportService.GenerateReportAsync(reportDate, subscriptionFilter: subscriptionId);
            var html = _htmlBuilder.BuildReport(report, anomalyReport: anomalyReport);
            await _reportStorageService.SaveHtmlAsync(PreGeneratedReportStorageService.BuildSubscriptionPath(reportDate, subscriptionId), html);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao gerar relatório da subscription {subscriptionId} para {date}", subscriptionId, reportDate.ToString("yyyy-MM-dd"));
            return false;
        }
    }

    private async Task<bool> GenerateTeamReportAsync(DateTime reportDate, TeamConfig team, CostAnomalyReport? anomalyReport)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(team.Id))
            {
                _logger.LogWarning("Time sem Id ignorado na pré-geração de relatório");
                return false;
            }

            var report = await _reportService.GenerateReportByTeamAsync(reportDate, team.Id);
            var teamName = string.IsNullOrWhiteSpace(team.Name) ? team.Id : team.Name;
            var html = _htmlBuilder.BuildReport(report, teamName, anomalyReport);
            await _reportStorageService.SaveHtmlAsync(PreGeneratedReportStorageService.BuildTeamPath(reportDate, team.Id), html);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao gerar relatório do time {teamId} para {date}", team.Id, reportDate.ToString("yyyy-MM-dd"));
            return false;
        }
    }
}
