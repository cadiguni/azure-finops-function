using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Builder para gerar relatórios em formato HTML
/// </summary>
public class HtmlReportBuilder
{
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
        BuildActionSummary(html, report);
        BuildAnomalySection(html, anomalyReport);
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
                        background-color: #f5f5f5; 
                        line-height: 1.6; 
                    }
                    .container { 
                        max-width: 1200px; margin: 0 auto; 
                        background: white; padding: 30px; 
                        border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); 
                    }
                    .header { 
                        text-align: center; margin-bottom: 30px; 
                        border-bottom: 3px solid #0078d4; padding-bottom: 20px; 
                    }
                    .team-badge {
                        display: inline-block;
                        background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
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
                        background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); 
                        color: white; padding: 20px; border-radius: 8px; 
                        text-align: center; box-shadow: 0 4px 6px rgba(0,0,0,0.1); 
                    }
                    .card h3 { margin: 0 0 10px 0; font-size: 1.1em; opacity: 0.9; }
                    .card .value { font-size: 1.8em; font-weight: bold; margin: 0; }
                    .action-excluir { background: linear-gradient(135deg, #ff4757 0%, #ff3838 100%); }
                    .action-reduzir { background: linear-gradient(135deg, #ff6b35 0%, #f7931e 100%); }
                    .action-revisar { background: linear-gradient(135deg, #feca57 0%, #ff9ff3 100%); }
                    .action-monitorar { background: linear-gradient(135deg, #48dbfb 0%, #0abde3 100%); }
                    table { width: 100%; border-collapse: collapse; margin-top: 20px; }
                    th, td { padding: 12px; text-align: left; border-bottom: 1px solid #ddd; }
                    th { background-color: #0078d4; color: white; font-weight: 600; }
                    tr:hover { background-color: #f8f9fa; }
                    .priority-high { color: #dc3545; font-weight: bold; }
                    .priority-medium { color: #ffc107; font-weight: bold; }
                    .priority-low { color: #28a745; }
                    .mg-section { margin: 30px 0; border: 1px solid #e9ecef; border-radius: 8px; }
                    .mg-header { 
                        background: #0078d4; color: white; padding: 15px 20px; 
                        margin: 0; border-radius: 8px 8px 0 0; 
                    }
                    .mg-content { padding: 20px; }
                    .currency { font-weight: bold; color: #28a745; }
                    .meta { color: #6c757d; font-size: 0.9em; margin-top: 30px; text-align: center; }
                    /* Anomaly Section */
                    .anomaly-section { margin: 30px 0; }
                    .anomaly-summary-cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 20px; margin-bottom: 20px; }
                    .anomaly-card { padding: 20px; border-radius: 8px; text-align: center; color: white; box-shadow: 0 4px 6px rgba(0,0,0,0.1); }
                    .anomaly-card h3 { margin: 0 0 8px 0; font-size: 1em; opacity: 0.9; }
                    .anomaly-card .value { font-size: 1.6em; font-weight: bold; }
                    .anomaly-card-info { background: linear-gradient(135deg, #4e54c8 0%, #8f94fb 100%); }
                    .anomaly-card-warn { background: linear-gradient(135deg, #f093fb 0%, #f5576c 100%); }
                    .anomaly-card-money { background: linear-gradient(135deg, #43e97b 0%, #38f9d7 100%); }
                    .severity-critical { background-color: #f8d7da; color: #721c24; font-weight: bold; padding: 2px 8px; border-radius: 4px; }
                    .severity-high { background-color: #fff3cd; color: #856404; font-weight: bold; padding: 2px 8px; border-radius: 4px; }
                    .severity-medium { background-color: #d1ecf1; color: #0c5460; font-weight: bold; padding: 2px 8px; border-radius: 4px; }
                    .severity-none { background-color: #d4edda; color: #155724; padding: 2px 8px; border-radius: 4px; }
                    .anomaly-note { background: #fff8e1; border-left: 4px solid #ffc107; padding: 12px 16px; border-radius: 4px; margin-top: 16px; color: #5d4037; font-size: 0.95em; }
                </style>
            </head>
            <body>
            <div class="container">
            """);

        var teamBadgeHtml = !string.IsNullOrEmpty(teamName) 
            ? $"<div class=\"team-badge\">👥 Time: {teamName}</div>" 
            : "";

        // Converter UTC para horário de Brasília (UTC-3)
        var generatedAtBrasilia = report.GeneratedAt.AddHours(-3);
        
        html.AppendLine($"""
                <div class="header">
                    <h1>🎯 Relatório FinOps - Recomendações de Otimização</h1>
                    <p><strong>Análise:</strong> {report.AnalysisDate:dd/MM/yyyy} | 
                       <strong>Gerado em:</strong> {generatedAtBrasilia:dd/MM/yyyy HH:mm} (Brasília)</p>
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
                    <p class="value currency">R$ {report.Summary.TotalPotentialSavings:N2}</p>
                </div>
                <div class="card">
                    <h3>Economia Anual Projetada</h3>
                    <p class="value currency">R$ {report.Summary.TotalPotentialSavings * 12:N2}</p>
                </div>
                <div class="card">
                    <h3>Management Groups</h3>
                    <p class="value">{report.ManagementGroups.Count}</p>
                </div>
            """);

        html.AppendLine("</div>");
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
                    <h3>{action.Key}</h3>
                    <p class="value">{action.Value} recursos</p>
                    <small>R$ {savings:N2}/mês</small>
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
            html.AppendLine("<p style=\"color:#6c757d;\">⚠️ Relatório de anomalias não disponível para esta data. Execute <code>POST /api/cost-anomalies/run</code> para gerar.</p>");
            html.AppendLine("</div>");
            return;
        }

        var alertSubscriptions = anomalyReport.Subscriptions.Where(s => s.HasAnomaly).ToList();
        var maxPct = alertSubscriptions.Any() ? alertSubscriptions.Max(s => s.IncreasePercent) : 0m;
        var maxAbs = alertSubscriptions.Any() ? alertSubscriptions.Max(s => s.IncreaseAmount) : 0m;
        var totalProjection = alertSubscriptions.Sum(s => s.MonthlyProjection);

        html.AppendLine($"""
            <p style="color:#6c757d;">
                📅 Data: <strong>{anomalyReport.Date}</strong> &nbsp;|
                💰 Orçamento mensal: <strong>R$ {anomalyReport.MonthlyBudget:N2}</strong> &nbsp;|
                📊 Meta diária: <strong>R$ {anomalyReport.DailyBudget:N2}</strong> &nbsp;|
                🔍 Baseline: <strong>{anomalyReport.BaselineDays} dias</strong>
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
                <h3>Maior Variação R$</h3>
                <p class="value">R$ {maxAbs:N2}</p>
            </div>
            <div class="anomaly-card anomaly-card-money">
                <h3>Projeção Mensal (alertas)</h3>
                <p class="value">R$ {totalProjection:N2}</p>
            </div>
            """);
        html.AppendLine("</div>");

        if (!alertSubscriptions.Any())
        {
            html.AppendLine($"<p style=\"color:#28a745;\">✅ Nenhuma anomalia detectada em {anomalyReport.TotalSubscriptionsAnalyzed} assinatura(s) analisada(s).</p>");
        }
        else
        {
            html.AppendLine("<table>");
            html.AppendLine("""
                <thead>
                    <tr>
                        <th>Subscription</th>
                        <th>Custo Atual</th>
                        <th>Média Últimos Dias</th>
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
                var reasons = string.Join("<br/>", sub.Reasons);
                var overBudget = sub.ProjectedOverBudget > 0 ? $"R$ {sub.ProjectedOverBudget:N2}" : "—";

                html.AppendLine($"""
                    <tr>
                        <td><strong>{sub.SubscriptionName}</strong><br/><small style="color:#6c757d;">{sub.SubscriptionId}</small></td>
                        <td class="currency">R$ {sub.TodayCost:N2}</td>
                        <td class="currency">R$ {sub.AverageLast3Days:N2}</td>
                        <td class="currency">{sign}R$ {sub.IncreaseAmount:N2}</td>
                        <td><strong>{sign}{sub.IncreasePercent:N1}%</strong></td>
                        <td class="currency">R$ {anomalyReport.DailyBudget:N2}</td>
                        <td class="currency">R$ {sub.MonthlyProjection:N2}</td>
                        <td>{overBudget}</td>
                        <td><span class="{severityClass}">{sub.Severity}</span></td>
                        <td style="font-size:0.85em;">{reasons}</td>
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
                        🏢 {mg.Name} ({mg.Id})
                        <span style="float: right;">
                            {mg.TotalRecommendations} recomendações | R$ {mg.TotalSavings:N2}/mês
                        </span>
                    </h3>
                    <div class="mg-content">
                """);

            foreach (var subscription in mg.Subscriptions)
            {
                // Mostra nome e ID (se o nome for diferente do ID)
                var subscriptionDisplay = subscription.Name != subscription.Id 
                    ? $"{subscription.Name} <small style=\"color:#6c757d;\">({subscription.Id})</small>"
                    : subscription.Id;
                    
                html.AppendLine($"""
                    <h4>🔹 Subscription: {subscriptionDisplay}</h4>
                    <p><em>{subscription.TotalRecommendations} recomendações | R$ {subscription.TotalSavings:N2}/mês</em></p>
                    """);

                foreach (var rg in subscription.ResourceGroups)
                {
                    if (rg.Recommendations.Count == 0) continue;

                    html.AppendLine($"<h5>📁 Resource Group: {rg.Name} ({rg.Location})</h5>");
                    html.AppendLine("<table>");
                    html.AppendLine("""
                        <thead>
                            <tr>
                                <th>Recurso</th>
                                <th>Tipo</th>
                                <th>Ação</th>
                                <th>Prioridade</th>
                                <th>Custo Diário</th>
                                <th>~Custo Mensal</th>
                                <th>Economia Mensal</th>
                                <th>Descrição</th>
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
                                <td><strong>{rec.ResourceName}</strong></td>
                                <td>{rec.ResourceType}</td>
                                <td><strong>{rec.Action}</strong></td>
                                <td class="{priorityClass}">{rec.Priority}</td>
                                <td class="currency">R$ {rec.DailyCost:N2}</td>
                                <td class="currency">~R$ {rec.CurrentCost:N2}</td>
                                <td class="currency">R$ {rec.PotentialSavings:N2}</td>
                                <td>{rec.Description}</td>
                            </tr>
                            """);
                    }

                    html.AppendLine("</tbody></table>");
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
}