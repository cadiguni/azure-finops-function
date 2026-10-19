using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Builder para gerar relatórios em formato HTML
/// </summary>
public class HtmlReportBuilder
{
    private static readonly CultureInfo ReportCulture = CultureInfo.GetCultureInfo("pt-BR");
    private readonly ILogger<HtmlReportBuilder> _logger;

    public HtmlReportBuilder(ILogger<HtmlReportBuilder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gera relatório HTML a partir dos dados de recomendações
    /// </summary>
    public string BuildReport(RecommendationReport report, string? teamName = null, CostAnomalyReport? anomalyReport = null)
    {
        _logger.LogInformation("🎨 Gerando relatório HTML para {date}, team: {team}", 
            report.AnalysisDate.ToString("yyyy-MM-dd"), teamName ?? "todos");

        var html = new StringBuilder();
        
        BuildHtmlHeader(html, report, teamName);
        BuildExecutiveSummary(html, report);
        BuildFinancialSummary(html, report);
        BuildActionSummary(html, report);
        if (anomalyReport != null)
        {
            BuildAnomalySection(html, anomalyReport);
        }
        BuildDetailedRecommendations(html, report);
        BuildHtmlFooter(html, report);

        _logger.LogInformation("✅ Relatório HTML gerado: {size} caracteres", html.Length);
        return html.ToString();
    }

    private void BuildHtmlHeader(StringBuilder html, RecommendationReport report, string? teamName = null)
    {
        html.AppendLine("""
            <!DOCTYPE html>
            <html lang="pt-BR">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>Relatório FinOps - Recomendações de Otimização</title>
                <style>
                    body { 
                        font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; 
                        margin: 0; padding: 20px; 
                        background-color: #0f1117; 
                        color: #e4e6ef;
                        line-height: 1.6; 
                    }
                    .container { 
                        max-width: 1200px; margin: 0 auto; 
                        background: #1e2130; padding: 30px; 
                        border-radius: 8px; border: 1px solid #2a2d3e;
                        box-shadow: 0 8px 24px rgba(0,0,0,0.35); 
                    }
                    .header { 
                        text-align: center; margin-bottom: 30px; 
                        border-bottom: 1px solid #353849; padding-bottom: 20px; 
                    }
                    .team-badge {
                        display: inline-block;
                        background: #252838;
                        color: white;
                        padding: 8px 20px;
                        border-radius: 20px;
                        font-size: 1.1em;
                        font-weight: bold;
                        margin-top: 10px;
                    }
                    .summary-cards { 
                        display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); 
                        gap: 20px; margin-bottom: 30px; 
                    }
                    .card { 
                        background: #252838; 
                        color: white; padding: 20px; border-radius: 8px; 
                        text-align: center; border: 1px solid #353849;
                        box-shadow: 0 4px 12px rgba(0,0,0,0.25); 
                    }
                    .card h3 { margin: 0 0 10px 0; font-size: 1.1em; opacity: 0.9; }
                    .card .value { font-size: 1.8em; font-weight: bold; margin: 0; }
                    .action-excluir { background: linear-gradient(135deg, #ff4757 0%, #ff3838 100%); }
                    .action-reduzir { background: linear-gradient(135deg, #ff6b35 0%, #f7931e 100%); }
                    .action-revisar { background: linear-gradient(135deg, #feca57 0%, #ff9ff3 100%); }
                    .action-monitorar { background: linear-gradient(135deg, #48dbfb 0%, #0abde3 100%); }
                    table { width: 100%; border-collapse: collapse; margin-top: 20px; }
                    th, td { padding: 12px; text-align: left; border-bottom: 1px solid #2a2d3e; }
                    th { background-color: #1a1d27; color: #9ca3b8; font-weight: 600; }
                    tr:hover { background-color: #252838; }
                    .table-scroll { width: 100%; overflow-x: auto; margin-top: 12px; }
                    .recommendations-table { min-width: 1080px; margin-top: 0; table-layout: fixed; }
                    .recommendations-table th, .recommendations-table td { vertical-align: top; }
                    .recommendations-table .nowrap { white-space: nowrap; }
                    .recommendation-description-row td {
                        padding: 10px 14px 18px;
                        background: #1a1d27;
                        border-bottom: 2px solid #353849;
                    }
                    .recommendation-description {
                        white-space: pre-wrap;
                        overflow-wrap: anywhere;
                        line-height: 1.5;
                    }
                    .cost-evidence { color: #9ca3b8; font-size: 0.86em; margin-top: 6px; }
                    .cost-evidence code { overflow-wrap: anywhere; white-space: normal; }
                    .priority-high { color: #ef4444; font-weight: bold; }
                    .priority-medium { color: #f59e0b; font-weight: bold; }
                    .priority-low { color: #22c55e; }
                    .mg-section { margin: 30px 0; border: 1px solid #2a2d3e; border-radius: 8px; }
                    .mg-header { 
                        background: #1a1d27; color: #e4e6ef; padding: 15px 20px; 
                        margin: 0; border-radius: 8px 8px 0 0; 
                    }
                    .mg-content { padding: 20px; }
                    .currency { font-weight: bold; color: #22c55e; }
                    .meta { color: #9ca3b8; font-size: 0.9em; margin-top: 30px; text-align: center; }
                    h1, h2, h3, h4, h5 { color: #e4e6ef; }
                    p, li, small, em { color: #cbd5e1; }
                    code { background: #252838; color: #e4e6ef; padding: 2px 6px; border-radius: 4px; }
                    /* Anomaly Section */
                    .anomaly-section { margin: 30px 0; }
                    .anomaly-summary-cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 20px; margin-bottom: 20px; }
                    .anomaly-card { padding: 20px; border-radius: 8px; text-align: center; color: white; box-shadow: 0 4px 6px rgba(0,0,0,0.1); }
                    .anomaly-card h3 { margin: 0 0 8px 0; font-size: 1em; opacity: 0.9; }
                    .anomaly-card .value { font-size: 1.6em; font-weight: bold; }
                    .anomaly-card-info { background: linear-gradient(135deg, #4e54c8 0%, #8f94fb 100%); }
                    .anomaly-card-warn { background: linear-gradient(135deg, #f093fb 0%, #f5576c 100%); }
                    .anomaly-card-money { background: linear-gradient(135deg, #43e97b 0%, #38f9d7 100%); }
                    .severity-critical { background-color: rgba(239, 68, 68, 0.15); color: #fca5a5; font-weight: bold; padding: 2px 8px; border-radius: 4px; }
                    .severity-high { background-color: rgba(245, 158, 11, 0.15); color: #fcd34d; font-weight: bold; padding: 2px 8px; border-radius: 4px; }
                    .severity-medium { background-color: rgba(59, 130, 246, 0.15); color: #93c5fd; font-weight: bold; padding: 2px 8px; border-radius: 4px; }
                    .severity-none { background-color: rgba(34, 197, 94, 0.15); color: #86efac; padding: 2px 8px; border-radius: 4px; }
                    .anomaly-note { background: #1a1d27; border-left: 4px solid #f59e0b; padding: 12px 16px; border-radius: 4px; margin-top: 16px; color: #e4e6ef; font-size: 0.95em; }
                </style>
            </head>
            <body>
            <div class="container">
            """);

        var teamBadgeHtml = !string.IsNullOrEmpty(teamName)
            ? $"<div class=\"team-badge\">👥 Time: {Encode(teamName)}</div>"
            : "";

        // Converter UTC para horário de Brasília (UTC-3)
        var generatedAtBrasilia = report.GeneratedAt.AddHours(-3);
        
        html.AppendLine($"""
                <div class="header">
                    <h1>🎯 Relatório FinOps - Recomendações de Otimização</h1>
                    <p><strong>Análise:</strong> {report.AnalysisDate:dd/MM/yyyy} | 
                       <strong>Gerado em:</strong> {generatedAtBrasilia:dd/MM/yyyy HH:mm} (Brasília)</p>
                          <p><strong>Período financeiro:</strong> {FormatReportPeriod(report)} |
                              <strong>Revisão:</strong> {Encode(report.SnapshotRevision ?? "não versionado")}</p>
                    {teamBadgeHtml}
                </div>
            """);
    }

    private void BuildExecutiveSummary(StringBuilder html, RecommendationReport report)
    {
        html.AppendLine("""
            <h2>📊 Resumo Executivo</h2>
            <div class="summary-cards">
            """);

        html.AppendLine($"""
                <div class="card">
                    <h3>Total de Recomendações</h3>
                    <p class="value">{report.Summary.TotalRecommendations}</p>
                </div>
                <div class="card">
                    <h3>Potencial de Economia Mensal</h3>
                    <p class="value currency">{FormatSavings(report.Summary.TotalPotentialSavings, report.Summary.SavingsCurrency ?? report.Currency)}</p>
                </div>
                <div class="card">
                    <h3>Economia Anual Projetada</h3>
                    <p class="value currency">{FormatSavings(report.Summary.TotalPotentialSavings * 12, report.Summary.SavingsCurrency ?? report.Currency)}</p>
                </div>
                <div class="card">
                    <h3>Custo observado associado</h3>
                    <p class="value currency">{FormatObservedCostTotal(report.Summary.ObservedCostTotal, report.Currency)}</p>
                </div>
                <div class="card">
                    <h3>Management Groups</h3>
                    <p class="value">{report.ManagementGroups.Count}</p>
                </div>
            """);

        html.AppendLine("</div>");
    }

    private static void BuildFinancialSummary(StringBuilder html, RecommendationReport report)
    {
        var financial = report.FinancialSummary;
        if (financial == null)
        {
            html.AppendLine("<p style=\"color:#9ca3b8;\">Resumo financeiro indisponível para este snapshot.</p>");
            return;
        }

        html.AppendLine("<h2>💰 Custos observados</h2>");
        html.AppendLine("<div class=\"summary-cards\">");
        html.AppendLine($"""
            <div class="card">
                <h3>Custo dos ciclos</h3>
                <p class="value currency">{FormatMoney(financial.TotalCost, financial.Currency ?? report.Currency)}</p>
            </div>
            <div class="card">
                <h3>Projeção dos ciclos</h3>
                <p class="value currency">{FormatMoney(financial.MonthlyProjection, financial.Currency ?? report.Currency)}</p>
            </div>
            <div class="card">
                <h3>Cobertura</h3>
                <p class="value">{financial.AvailableSubscriptions}/{financial.ExpectedSubscriptions}</p>
            </div>
            <div class="card">
                <h3>Qualidade</h3>
                <p class="value">{Encode(FormatAvailability(financial.Availability))}</p>
            </div>
            """);
        html.AppendLine("</div>");
        html.AppendLine($"""
            <p style="color:#9ca3b8;">
                Dados até <strong>{Encode(financial.LatestDataAsOf ?? "não informado")}</strong>;
                estado <strong>{Encode(FormatFinality(financial.Finality))}</strong>;
                fontes <strong>{Encode(string.Join(", ", financial.Sources.DefaultIfEmpty("não informada")))}</strong>;
                revisões diárias <strong>{financial.DailyRevisions.Count}</strong>.
            </p>
            """);
    }

    private void BuildActionSummary(StringBuilder html, RecommendationReport report)
    {
        html.AppendLine("<h2>🎯 Distribuição por Ações</h2>");
        html.AppendLine("<div class=\"summary-cards\">");

        var actionColors = new Dictionary<string, string>
        {
            { "Excluir", "action-excluir" },
            { "Reduzir", "action-reduzir" }, 
            { "Revisar", "action-revisar" },
            { "Monitorar", "action-monitorar" }
        };

        foreach (var action in report.Summary.ActionBreakdown)
        {
            var colorClass = actionColors.GetValueOrDefault(action.Key, "card");
            var savings = report.Summary.SavingsByAction.GetValueOrDefault(action.Key, 0m);
            
            html.AppendLine($"""
                <div class="card {colorClass}">
                    <h3>{Encode(action.Key)}</h3>
                    <p class="value">{action.Value} recursos</p>
                    <small>{FormatActionSavings(savings, report.Summary.SavingsCurrency ?? report.Currency)}</small>
                </div>
                """);
        }

        html.AppendLine("</div>");
    }

    private void BuildAnomalySection(StringBuilder html, CostAnomalyReport? anomalyReport)
    {
        html.AppendLine("<div class=\"anomaly-section\">");
        html.AppendLine("<h2>📈 Anomalias de Custo Diário</h2>");

        if (anomalyReport == null)
        {
            html.AppendLine("<p style=\"color:#9ca3b8;\">⚠️ Relatório de anomalias não disponível para esta data. Execute <code>POST /api/cost-anomalies/run</code> para gerar.</p>");
            html.AppendLine("</div>");
            return;
        }

        var alertSubscriptions = anomalyReport.Subscriptions.Where(s => s.HasAnomaly).ToList();
        var insufficientDataSubscriptions = anomalyReport.Subscriptions
            .Where(s => s.DataStatus == CostAnomalyDataStatus.InsufficientData)
            .ToList();
        var noCostDataSubscriptions = anomalyReport.Subscriptions
            .Where(s => s.DataStatus == CostAnomalyDataStatus.NoCostData)
            .ToList();
        var maxPct = alertSubscriptions.Any() ? alertSubscriptions.Max(s => s.IncreasePercent) : 0m;
        var maxAbs = alertSubscriptions.Any() ? alertSubscriptions.Max(s => s.IncreaseAmount) : 0m;
        var totalProjection = alertSubscriptions.Sum(s => s.MonthlyProjection);
        var requestedDate = string.IsNullOrWhiteSpace(anomalyReport.RequestedDate) ? anomalyReport.Date : anomalyReport.RequestedDate;
        var baselinePeriod = string.IsNullOrWhiteSpace(anomalyReport.BaselineStartDate)
            ? "n/d"
            : $"{anomalyReport.BaselineStartDate} a {anomalyReport.BaselineEndDate}";

        html.AppendLine($"""
            <p style="color:#9ca3b8;">
                📅 Data solicitada: <strong>{requestedDate}</strong> &nbsp;|
                📌 Data analisada: <strong>{(string.IsNullOrWhiteSpace(anomalyReport.AnalysisDate) ? "n/d" : anomalyReport.AnalysisDate)}</strong> &nbsp;|
                🧮 Baseline: <strong>{baselinePeriod}</strong> &nbsp;|
                💰 Orçamento mensal: <strong>{FormatMoney(anomalyReport.MonthlyBudget, anomalyReport.Currency)}</strong> &nbsp;|
                📊 Meta diária: <strong>{FormatMoney(anomalyReport.DailyBudget, anomalyReport.Currency)}</strong> &nbsp;|
                🔍 Dias baseline: <strong>{anomalyReport.BaselineDays}</strong>
            </p>
            """);

        html.AppendLine("<div class=\"anomaly-summary-cards\">");
        html.AppendLine($"""
            <div class="anomaly-card anomaly-card-warn">
                <h3>Assinaturas em Alerta</h3>
                <p class="value">{alertSubscriptions.Count} / {anomalyReport.TotalSubscriptionsAnalyzed}</p>
            </div>
            <div class="anomaly-card anomaly-card-info">
                <h3>Maior Variação %</h3>
                <p class="value">+{maxPct:N1}%</p>
            </div>
            <div class="anomaly-card anomaly-card-info">
                <h3>Maior Variação ({Encode(anomalyReport.Currency)})</h3>
                <p class="value">{FormatMoney(maxAbs, anomalyReport.Currency)}</p>
            </div>
            <div class="anomaly-card anomaly-card-money">
                <h3>Projeção Mensal (alertas)</h3>
                <p class="value">{FormatMoney(totalProjection, anomalyReport.Currency)}</p>
            </div>
            """);
        html.AppendLine("</div>");

        if (!alertSubscriptions.Any())
        {
            html.AppendLine($"<p style=\"color:#22c55e;\">✅ Nenhuma anomalia detectada em {anomalyReport.TotalSubscriptionsAnalyzed} assinatura(s) analisada(s).</p>");
        }
        else
        {
            html.AppendLine("<table>");
            html.AppendLine("""
                <thead>
                    <tr>
                        <th>Subscription</th>
                        <th>Data analisada</th>
                        <th>Último custo diário disponível</th>
                        <th>Média baseline</th>
                        <th>Período baseline</th>
                        <th>Diferença</th>
                        <th>Variação %</th>
                        <th>Meta Diária</th>
                        <th>Projeção Mensal</th>
                        <th>Estouro Projetado</th>
                        <th>Severidade</th>
                        <th>Motivos</th>
                    </tr>
                </thead>
                <tbody>
                """);

            foreach (var sub in alertSubscriptions.OrderByDescending(s => s.IncreasePercent))
            {
                var severityClass = sub.Severity.ToLowerInvariant() switch
                {
                    "critical" => "severity-critical",
                    "high" => "severity-high",
                    "medium" => "severity-medium",
                    _ => "severity-none"
                };
                var sign = sub.IncreaseAmount >= 0 ? "+" : "";
                var reasons = string.Join("<br/>", sub.Reasons.Select(Encode));
                var overBudget = sub.ProjectedOverBudget > 0
                    ? FormatMoney(sub.ProjectedOverBudget, anomalyReport.Currency)
                    : "—";

                html.AppendLine($"""
                    <tr>
                        <td><strong>{Encode(sub.SubscriptionName)}</strong><br/><small style="color:#9ca3b8;">{Encode(sub.SubscriptionId)}</small></td>
                        <td>{Encode(sub.AnalysisDate)}</td>
                        <td class="currency">{FormatMoney(sub.LatestDailyCost, anomalyReport.Currency)}</td>
                        <td class="currency">{FormatMoney(sub.BaselineAverage, anomalyReport.Currency)}</td>
                        <td>{Encode(sub.BaselineStartDate)} a {Encode(sub.BaselineEndDate)}</td>
                        <td class="currency">{sign}{FormatMoney(sub.IncreaseAmount, anomalyReport.Currency)}</td>
                        <td><strong>{sign}{sub.IncreasePercent:N1}%</strong></td>
                        <td class="currency">{FormatMoney(anomalyReport.DailyBudget, anomalyReport.Currency)}</td>
                        <td class="currency">{FormatMoney(sub.MonthlyProjection, anomalyReport.Currency)}</td>
                        <td>{overBudget}</td>
                        <td><span class="{severityClass}">{Encode(sub.Severity)}</span></td>
                        <td style="font-size:0.85em;">{reasons}</td>
                    </tr>
                    """);
            }

            html.AppendLine("</tbody></table>");
        }

        if (insufficientDataSubscriptions.Any() || noCostDataSubscriptions.Any())
        {
            html.AppendLine("<h3 style=\"margin-top:20px;\">ℹ️ Subscriptions sem dados suficientes</h3>");
            html.AppendLine("<table>");
            html.AppendLine("""
                <thead>
                    <tr>
                        <th>Subscription</th>
                        <th>Status</th>
                        <th>Data solicitada</th>
                        <th>Data analisada</th>
                        <th>Período baseline</th>
                    </tr>
                </thead>
                <tbody>
                """);

            foreach (var sub in insufficientDataSubscriptions.Concat(noCostDataSubscriptions))
            {
                html.AppendLine($"""
                    <tr>
                        <td><strong>{Encode(sub.SubscriptionName)}</strong><br/><small style="color:#9ca3b8;">{Encode(sub.SubscriptionId)}</small></td>
                        <td>{Encode(sub.DataStatus)}</td>
                        <td>{Encode(sub.RequestedDate)}</td>
                        <td>{Encode(string.IsNullOrWhiteSpace(sub.AnalysisDate) ? "n/d" : sub.AnalysisDate)}</td>
                        <td>{Encode(string.IsNullOrWhiteSpace(sub.BaselineStartDate) ? "n/d" : $"{sub.BaselineStartDate} a {sub.BaselineEndDate}")}</td>
                    </tr>
                    """);
            }

            html.AppendLine("</tbody></table>");
        }

        html.AppendLine("""
            <div class="anomaly-note">
                ⚠️ <strong>Nota:</strong> Esta análise identifica variações anormais de custo com base no comportamento
                recente e na meta diária configurada. A recomendação inicial é revisar a assinatura e identificar
                quais recursos contribuíram para o aumento.
            </div>
            """);

        html.AppendLine("</div>");
    }

    private void BuildDetailedRecommendations(StringBuilder html, RecommendationReport report)
    {
        html.AppendLine("<h2>📋 Recomendações Detalhadas</h2>");

        foreach (var mg in report.ManagementGroups)
        {
            html.AppendLine($"""
                <div class="mg-section">
                    <h3 class="mg-header">
                        🏢 {Encode(mg.Name)} ({Encode(mg.Id)})
                        <span style="float: right;">
                            {mg.TotalRecommendations} recomendações | {FormatMoney(mg.TotalSavings, report.Summary.SavingsCurrency ?? report.Currency)}/mês
                        </span>
                    </h3>
                    <div class="mg-content">
                """);

            foreach (var subscription in mg.Subscriptions)
            {
                // Mostra nome e ID (se o nome for diferente do ID)
                var subscriptionDisplay = subscription.Name != subscription.Id 
                    ? $"{Encode(subscription.Name)} <small style=\"color:#9ca3b8;\">({Encode(subscription.Id)})</small>"
                    : Encode(subscription.Id);
                    
                html.AppendLine($"""
                    <h4>🔹 Subscription: {subscriptionDisplay}</h4>
                    <p><em>{subscription.TotalRecommendations} recomendações | {FormatMoney(subscription.TotalSavings, report.Summary.SavingsCurrency ?? report.Currency)}/mês</em></p>
                    """);

                foreach (var rg in subscription.ResourceGroups)
                {
                    if (rg.Recommendations.Count == 0) continue;

                    html.AppendLine($"<h5>📁 Resource Group: {Encode(rg.Name)} ({Encode(rg.Location)})</h5>");
                    html.AppendLine("<div class=\"table-scroll\"><table class=\"recommendations-table\">");
                    html.AppendLine("""
                        <colgroup>
                            <col style="width:17%" />
                            <col style="width:17%" />
                            <col style="width:9%" />
                            <col style="width:8%" />
                            <col style="width:11%" />
                            <col style="width:11%" />
                            <col style="width:13%" />
                            <col style="width:14%" />
                        </colgroup>
                        <thead>
                            <tr>
                                <th>Recurso</th>
                                <th>Tipo</th>
                                <th>Ação</th>
                                <th>Prioridade</th>
                                <th>Custo Observado</th>
                                <th>Projeção Mensal</th>
                                <th>Economia Mensal</th>
                                <th>Origem / Período</th>
                            </tr>
                        </thead>
                        <tbody>
                        """);

                    foreach (var rec in rg.Recommendations)
                    {
                        var priorityClass = rec.Priority.ToLowerInvariant() switch
                        {
                            "high" => "priority-high",
                            "medium" => "priority-medium", 
                            "low" => "priority-low",
                            _ => ""
                        };

                        html.AppendLine($"""
                            <tr>
                                <td><strong>{Encode(rec.ResourceName)}</strong></td>
                                <td>{Encode(rec.ResourceType)}</td>
                                <td class="nowrap"><strong>{Encode(rec.Action)}</strong></td>
                                <td class="nowrap {priorityClass}">{Encode(rec.Priority)}</td>
                                <td class="currency nowrap">{FormatMoney(rec.ObservedCost, rec.Currency)}</td>
                                <td class="currency nowrap">~{FormatMoney(rec.CurrentCost, rec.Currency)}</td>
                                <td class="currency">{FormatRecommendationSavings(rec)}</td>
                                <td>{FormatCostMetadata(rec)}</td>
                            </tr>
                            <tr class="recommendation-description-row">
                                <td colspan="8">
                                    <div class="recommendation-description"><strong>Descrição:</strong> {Encode(rec.Description)}</div>
                                    {FormatCostEvidence(rec)}
                                </td>
                            </tr>
                            """);
                    }

                    html.AppendLine("</tbody></table></div>");
                }
            }

            html.AppendLine("</div></div>");
        }
    }

    private void BuildHtmlFooter(StringBuilder html, RecommendationReport report)
    {
        // Converter UTC para horário de Brasília (UTC-3)
        var generatedAtBrasilia = report.GeneratedAt.AddHours(-3);
        
        html.AppendLine($"""
            <div class="meta">
                <p>📈 <strong>Azure FinOps Platform</strong> | 
                   Relatório gerado automaticamente em {generatedAtBrasilia:dd/MM/yyyy HH:mm} (Brasília) | 
                   Dados de {report.AnalysisDate:dd/MM/yyyy}</p>
                <p><em>Este relatório identifica oportunidades de otimização de custos baseado em análise automatizada dos recursos Azure.</em></p>
            </div>
            </div>
            </body>
            </html>
            """);
    }

    private static string FormatReportPeriod(RecommendationReport report)
        => string.IsNullOrWhiteSpace(report.PeriodFrom) || string.IsNullOrWhiteSpace(report.PeriodTo)
            ? "ciclos de faturamento variam por assinatura"
            : $"{Encode(report.PeriodFrom)} a {Encode(report.PeriodTo)}";

    private static string FormatCostMetadata(ActionableRecommendation recommendation)
    {
        var source = recommendation.CostProvenance?.Source ?? "Unknown";
        var periodFrom = recommendation.CostPeriodFrom ?? ValidProvenanceDate(
            recommendation.CostProvenance?.RequestedFrom);
        var periodTo = recommendation.CostPeriodTo ?? ValidProvenanceDate(
            recommendation.CostProvenance?.RequestedTo);
        var period = periodFrom.HasValue && periodTo.HasValue
            ? $"{periodFrom:dd/MM/yyyy} a {periodTo:dd/MM/yyyy}"
            : "período indisponível";
        return $"{Encode(source)}<br/><small>{Encode(FormatCostScope(recommendation.CostScope))} | {period}</small>";
    }

    private static DateTime? ValidProvenanceDate(DateTime? value)
        => value.HasValue && value.Value.Year > 1 ? value.Value : null;

    private static string FormatCostScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return "Não associado";
        var parts = scope.Split(':', 2);
        var label = parts[0] switch
        {
            "Direct" => "Direto",
            "Shared" => "Compartilhado",
            "Approximate" => "Aproximado",
            "Missing" => "Não associado",
            _ => parts[0]
        };
        return parts.Length == 2 ? $"{label}: {parts[1]}" : label;
    }

    private static string FormatCostEvidence(ActionableRecommendation recommendation)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(recommendation.CostNote))
        {
            details.Add($"<strong>Qualidade do custo:</strong> {Encode(recommendation.CostNote)}");
        }
        if (!string.IsNullOrWhiteSpace(recommendation.CostMatchedResourceId))
        {
            details.Add($"<strong>Recurso cobrado:</strong> <code>{Encode(recommendation.CostMatchedResourceId)}</code>");
        }
        return details.Count == 0
            ? string.Empty
            : $"<div class=\"cost-evidence\">{string.Join("<br/>", details)}</div>";
    }

    private static string FormatMoney(decimal? value, string? currency)
    {
        if (!value.HasValue) return "não observado";
        if (string.IsNullOrWhiteSpace(currency)) return "moeda não agregável";
        return $"{Encode(currency)} {value.Value.ToString("N2", ReportCulture)}";
    }

    private static string FormatObservedCostTotal(decimal? value, string? currency)
        => value.HasValue ? FormatMoney(value, currency) : "não agregável com confiança";

    private static string FormatActionSavings(decimal value, string? currency)
        => value > 0 ? $"{FormatMoney(value, currency)}/mês" : "nenhuma economia quantificada";

    private static string FormatAvailability(string? value) => value switch
    {
        "Available" => "Disponível",
        "Partial" => "Parcial",
        "Missing" => "Sem dados",
        "Failed" => "Falha",
        "MixedCurrency" => "Moedas não agregáveis",
        _ => value ?? "Não informada"
    };

    private static string FormatFinality(string? value) => value switch
    {
        "Provisional" => "Provisório",
        "Reconciled" => "Reconciliado",
        "Final" => "Final",
        _ => value ?? "Não informado"
    };

    private static string FormatSavings(decimal value, string? currency)
        => value > 0 ? FormatMoney(value, currency) : "nenhuma economia quantificada";

    private static string FormatRecommendationSavings(ActionableRecommendation recommendation)
    {
        if (recommendation.SavingsStatus == "ExcludedFromTotal")
        {
            return "não somada (compartilhada/sobreposta)";
        }

        if ((recommendation.SavingsStatus == "Quantified" || string.IsNullOrWhiteSpace(recommendation.SavingsStatus))
            && recommendation.PotentialSavings > 0)
        {
            return FormatMoney(recommendation.PotentialSavings, recommendation.Currency);
        }

        // Sem economia calculada, informa o valor em jogo. É custo exposto, não economia, e por isso
        // segue fora de TotalPotentialSavings e de qualquer soma do relatório.
        return recommendation.CurrentCost > 0
            ? $"não estimada · custo exposto {FormatMoney(recommendation.CurrentCost, recommendation.Currency)}/mês"
            : "não estimada";
    }

    private static string Encode(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
