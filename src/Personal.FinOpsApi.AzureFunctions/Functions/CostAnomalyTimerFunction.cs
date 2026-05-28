using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// Timer e API manual para detecção de anomalias de custo diário.
/// 
/// Timer: Roda diariamente às 08:00 UTC (configurável via CostAnomalySchedule)
/// API:   GET /api/cost-anomalies?date=2026-05-14 (consulta relatório salvo)
/// API:   POST /api/cost-anomalies/run (execução manual)
/// 
/// Fluxo:
/// 1. Consulta custo diário das subscriptions via Cost Management API
/// 2. Compara hoje vs últimos 3 dias (baseline)
/// 3. Compara hoje vs meta diária (budget / 30)
/// 4. Calcula projeção mensal
/// 5. Gera JSON de anomalias no Blob
/// </summary>
public class CostAnomalyTimerFunction
{
    private readonly CostAnomalyDailyCostService _dailyCostService;
    private readonly CostAnomalyAnalysisService _analysisService;
    private readonly CostAnomalyStorageService _storageService;
    private readonly SubscriptionDiscoveryService _subscriptionDiscoveryService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostAnomalyTimerFunction> _logger;

    public CostAnomalyTimerFunction(
        CostAnomalyDailyCostService dailyCostService,
        CostAnomalyAnalysisService analysisService,
        CostAnomalyStorageService storageService,
        SubscriptionDiscoveryService subscriptionDiscoveryService,
        IConfiguration configuration,
        ILogger<CostAnomalyTimerFunction> logger)
    {
        _dailyCostService = dailyCostService;
        _analysisService = analysisService;
        _storageService = storageService;
        _subscriptionDiscoveryService = subscriptionDiscoveryService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Timer diário para detecção de anomalias de custo
    /// </summary>
    [Function("CostAnomalyDailyTimer")]
    public async Task RunTimer([TimerTrigger("%CostAnomalySchedule%")] TimerInfo timer)
    {
        var enabled = _configuration["EnableCostAnomalyAnalysis"];
        if (string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[COST-ANOMALY-TIMER] Feature desabilitada (EnableCostAnomalyAnalysis=false)");
            return;
        }
        _logger.LogInformation("[COST-ANOMALY-TIMER] Iniciando análise de anomalias de custo diário");
        await ExecuteAnomalyAnalysisAsync();
    }

    /// <summary>
    /// Execução manual da análise de anomalias
    /// POST /api/cost-anomalies/run
    /// </summary>
    [Function("CostAnomalyManualRun")]
    public async Task<HttpResponseData> ManualRun(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "cost-anomalies/run")] HttpRequestData req)
    {
        var enabled = _configuration["EnableCostAnomalyAnalysis"];
        if (string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            var disabledResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await disabledResponse.WriteAsJsonAsync(new { message = "Feature desabilitada (EnableCostAnomalyAnalysis=false)" });
            return disabledResponse;
        }

        _logger.LogInformation("[COST-ANOMALY] Execução manual iniciada");

        try
        {
            var report = await ExecuteAnomalyAnalysisAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(report);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[COST-ANOMALY] Erro na execução manual");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Erro interno ao executar análise de anomalias." });
            return errorResponse;
        }
    }

    /// <summary>
    /// Consulta relatório de anomalias salvo no blob
    /// GET /api/cost-anomalies?date=2026-05-14
    /// GET /api/cost-anomalies?days=3
    /// </summary>
    [Function("CostAnomalyGet")]
    public async Task<HttpResponseData> GetAnomalyReport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost-anomalies")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var date = query["date"] ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var subscriptionIdFilter = query["subscriptionId"];
        var severityFilter = query["severity"];
        var excludedSubscriptions = GetExcludedSubscriptions();
        var days = int.TryParse(query["days"], out var parsedDays)
            ? Math.Clamp(parsedDays, 1, 30)
            : 3;

        try
        {
            var baseDate = DateTime.TryParse(date, out var parsedDate) ? parsedDate.Date : DateTime.UtcNow.Date;
            var reports = new List<CostAnomalyReport>();

            for (var i = 0; i < days; i++)
            {
                var targetDate = baseDate.AddDays(-i).ToString("yyyy-MM-dd");
                var report = await _storageService.LoadAnomalyReportAsync(targetDate);
                if (report != null)
                {
                    reports.Add(report);
                }
            }

            if (reports.Count == 0)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteAsJsonAsync(new
                {
                    message = days > 1
                        ? $"Nenhum relatório de anomalias encontrado nos últimos {days} dia(s) a partir de {baseDate:yyyy-MM-dd}"
                        : $"Nenhum relatório de anomalias encontrado para {date}",
                    date,
                    days,
                    suggestion = "Execute POST /api/cost-anomalies/run para gerar"
                });
                return notFoundResponse;
            }

            // Aplicar filtros opcionais e retornar lista flat para Grafana
            var flattened = reports.SelectMany(report => report.Subscriptions.Select(s => new
            {
                date = report.Date,
                dailyBudget = report.DailyBudget,
                subscriptionId = s.SubscriptionId,
                subscriptionName = s.SubscriptionName,
                todayCost = s.TodayCost,
                averageLastDays = s.AverageLast3Days,
                increaseAmount = s.IncreaseAmount,
                increasePercent = s.IncreasePercent,
                monthlyProjection = s.MonthlyProjection,
                projectedOverBudget = s.ProjectedOverBudget,
                severity = s.Severity,
                hasAnomaly = s.HasAnomaly,
                reasons = s.Reasons
            }));

            flattened = flattened.Where(s => !excludedSubscriptions.Contains(s.subscriptionId));

            if (!string.IsNullOrEmpty(subscriptionIdFilter))
                flattened = flattened.Where(s => s.subscriptionId.Equals(subscriptionIdFilter, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(severityFilter))
                flattened = flattened.Where(s => s.severity.Equals(severityFilter, StringComparison.OrdinalIgnoreCase));

            var result = flattened
                .OrderByDescending(s => s.date)
                .ThenByDescending(s => s.increasePercent)
                .ToList();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(result);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[COST-ANOMALY] Erro ao consultar relatório de anomalias");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Erro interno ao consultar relatório de anomalias." });
            return errorResponse;
        }
    }

    /// <summary>
    /// Executa a análise de anomalias para todas as subscriptions configuradas
    /// </summary>
    private async Task<CostAnomalyReport> ExecuteAnomalyAnalysisAsync()
    {
        var config = CostAnomalyConfig.FromConfiguration(_configuration);
        var today = DateTime.UtcNow.Date;
        var excludedSubscriptions = GetExcludedSubscriptions();

        _logger.LogInformation(
            "[COST-ANOMALY] Config: Budget=R$ {budget}/mês, Meta diária=R$ {daily:F2}, Baseline={baseline} dias, MediumThreshold={medium}%",
            config.MonthlyBudget, config.DailyBudget, config.BaselineDays, config.MediumPercent);

        if (excludedSubscriptions.Count > 0)
        {
            _logger.LogInformation("[COST-ANOMALY] Excluindo {count} subscription(s) da análise: {ids}",
                excludedSubscriptions.Count, string.Join(",", excludedSubscriptions));
        }

        // Resolver subscriptions (mesmo padrão do CostByServiceDailyTimerFunction)
        var subscriptions = await ResolveSubscriptionsAsync();
        subscriptions = subscriptions
            .Where(s => !excludedSubscriptions.Contains(s))
            .ToList();
        var subscriptionNames = await ResolveSubscriptionNamesAsync(subscriptions);

        var report = new CostAnomalyReport
        {
            Date = today.ToString("yyyy-MM-dd"),
            MonthlyBudget = config.MonthlyBudget,
            DailyBudget = config.DailyBudget,
            BaselineDays = config.BaselineDays,
            TotalSubscriptionsAnalyzed = subscriptions.Count
        };

        foreach (var subscriptionId in subscriptions)
        {
            try
            {
                var subscriptionName = subscriptionNames.GetValueOrDefault(subscriptionId, subscriptionId);

                // 1. Buscar custos diários
                var dailyCosts = await _dailyCostService.GetDailyCostsAsync(subscriptionId, config.BaselineDays);

                if (dailyCosts.Count == 0)
                {
                    _logger.LogWarning("[COST-ANOMALY] Sem dados de custo para {subscriptionId}", subscriptionId);
                    continue;
                }

                // 2. Analisar anomalias
                var anomaly = _analysisService.Analyze(subscriptionId, subscriptionName, dailyCosts, config);
                report.Subscriptions.Add(anomaly);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[COST-ANOMALY] Erro ao analisar subscription {subscriptionId}", subscriptionId);
            }
        }

        report.TotalAnomaliesDetected = report.Subscriptions.Count(s => s.HasAnomaly);

        // 3. Salvar no blob
        await _storageService.SaveAnomalyReportAsync(report);

        _logger.LogInformation(
            "[COST-ANOMALY] Análise concluída: {total} subscriptions, {anomalies} anomalias detectadas",
            report.TotalSubscriptionsAnalyzed, report.TotalAnomaliesDetected);

        return report;
    }

    private async Task<List<string>> ResolveSubscriptionsAsync()
    {
        var raw = _configuration["COST_SUBSCRIPTIONS"];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        try
        {
            var discovered = await _subscriptionDiscoveryService.DiscoverSubscriptionsAsync();
            if (discovered.Count > 0)
            {
                _logger.LogInformation("[COST-ANOMALY] Discovery automático: {count} subscriptions", discovered.Count);
                return discovered.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[COST-ANOMALY] Falha no discovery automático");
        }

        var single = _configuration["AZURE_SUBSCRIPTION_ID"];
        if (!string.IsNullOrWhiteSpace(single))
            return new List<string> { single.Trim() };

        _logger.LogWarning("[COST-ANOMALY] Nenhuma subscription configurada");
        return new List<string>();
    }

    private async Task<Dictionary<string, string>> ResolveSubscriptionNamesAsync(List<string> subscriptionIds)
    {
        var names = new Dictionary<string, string>();
        try
        {
            var details = await _subscriptionDiscoveryService.GetSubscriptionDetailsAsync(subscriptionIds);
            foreach (var (id, detail) in details)
            {
                // detail é anonymous type com display_name
                var json = System.Text.Json.JsonSerializer.Serialize(detail);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("display_name", out var displayName))
                {
                    names[id] = displayName.GetString() ?? id;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[COST-ANOMALY] Falha ao resolver nomes das subscriptions");
        }

        return names;
    }

    private HashSet<string> GetExcludedSubscriptions()
    {
        var raw = _configuration["CostAnomalyExcludedSubscriptions"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
