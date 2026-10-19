using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Builder para gerar relatórios em formato CSV
/// </summary>
public class CsvReportBuilder
{
    private readonly ILogger<CsvReportBuilder> _logger;

    public CsvReportBuilder(ILogger<CsvReportBuilder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gera relatório CSV a partir dos dados de recomendações
    /// </summary>
    public string BuildReport(RecommendationReport report)
    {
        _logger.LogInformation("📄 Gerando relatório CSV para {date}", report.AnalysisDate.ToString("yyyy-MM-dd"));

        var csv = new StringBuilder();
        
        BuildCsvHeader(csv);
        BuildFinancialSummary(csv, report);
        BuildCsvData(csv, report);

        _logger.LogInformation("✅ Relatório CSV gerado: {lines} linhas", csv.ToString().Split('\n').Length);
        return csv.ToString();
    }

    private void BuildCsvHeader(StringBuilder csv)
    {
        csv.AppendLine("RecordType,ManagementGroup,ManagementGroupName,SubscriptionId,SubscriptionName,ResourceGroup,Location,ResourceName,ResourceType,ResourceId,Action,Priority,Confidence,Description,PotentialMonthlySavings,MonthlyProjection,AnalysisDate,ObservedCost,CostCurrency,CostScope,CostSource,CostBasis,CostDataAsOf,CostPeriodFrom,CostPeriodTo,DatasetRevision,SnapshotRevision,ReportPeriodFrom,ReportPeriodTo,FinancialTotalCost,FinancialMonthlyProjection,FinancialCurrency,FinancialAvailability,FinancialFinality,FinancialDataAsOf,FinancialSources,RecommendationObservedCostTotal,SavingsStatus");
    }

    private void BuildFinancialSummary(StringBuilder csv, RecommendationReport report)
    {
        var financial = report.FinancialSummary;
        var values = new string[38];
        Array.Fill(values, string.Empty);
        values[0] = "SnapshotSummary";
        values[16] = report.AnalysisDate.ToString("yyyy-MM-dd");
        values[26] = EscapeCsvValue(report.SnapshotRevision ?? string.Empty);
        values[27] = EscapeCsvValue(report.PeriodFrom);
        values[28] = EscapeCsvValue(report.PeriodTo);
        values[29] = financial?.TotalCost?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        values[30] = financial?.MonthlyProjection?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        values[31] = EscapeCsvValue(financial?.Currency ?? report.Currency);
        values[32] = EscapeCsvValue(financial?.Availability ?? "Missing");
        values[33] = EscapeCsvValue(financial?.Finality ?? "Provisional");
        values[34] = EscapeCsvValue(financial?.LatestDataAsOf ?? string.Empty);
        values[35] = EscapeCsvValue(string.Join("|", financial?.Sources ?? []));
        values[36] = report.Summary.ObservedCostTotal?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        csv.AppendLine(string.Join(",", values));
    }

    private void BuildCsvData(StringBuilder csv, RecommendationReport report)
    {
        foreach (var mg in report.ManagementGroups)
        {
            foreach (var subscription in mg.Subscriptions)
            {
                foreach (var rg in subscription.ResourceGroups)
                {
                    foreach (var rec in rg.Recommendations)
                    {
                        var line = BuildCsvLine(mg, subscription, rg, rec, report);
                        csv.AppendLine(line);
                    }
                }
            }
        }
    }

    private string BuildCsvLine(
        ManagementGroupReport mg, 
        SubscriptionReport subscription, 
        ResourceGroupReport rg, 
        ActionableRecommendation rec,
        RecommendationReport report)
    {
        var values = new[]
        {
            "Recommendation",
            EscapeCsvValue(mg.Id),
            EscapeCsvValue(mg.Name),
            EscapeCsvValue(subscription.Id),
            EscapeCsvValue(subscription.Name),
            EscapeCsvValue(rg.Name),
            EscapeCsvValue(rg.Location),
            EscapeCsvValue(rec.ResourceName),
            EscapeCsvValue(rec.ResourceType),
            EscapeCsvValue(rec.ResourceId),
            EscapeCsvValue(rec.Action),
            EscapeCsvValue(rec.Priority),
            EscapeCsvValue(rec.Confidence),
            EscapeCsvValue(rec.Description),
            rec.PotentialSavings.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            rec.CurrentCost.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            report.AnalysisDate.ToString("yyyy-MM-dd"),
            rec.ObservedCost?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            EscapeCsvValue(rec.Currency),
            EscapeCsvValue(rec.CostScope),
            EscapeCsvValue(rec.CostProvenance?.Source ?? "Unknown"),
            EscapeCsvValue(rec.CostProvenance?.CostBasis ?? ""),
            rec.CostProvenance?.DataAsOf?.ToString("O") ?? "",
            rec.CostPeriodFrom?.ToString("yyyy-MM-dd") ?? "",
            rec.CostPeriodTo?.ToString("yyyy-MM-dd") ?? "",
            EscapeCsvValue(rec.CostProvenance?.DatasetRevision ?? ""),
            EscapeCsvValue(report.SnapshotRevision ?? ""),
            EscapeCsvValue(report.PeriodFrom),
            EscapeCsvValue(report.PeriodTo),
            report.FinancialSummary?.TotalCost?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            report.FinancialSummary?.MonthlyProjection?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            EscapeCsvValue(report.FinancialSummary?.Currency ?? report.Currency),
            EscapeCsvValue(report.FinancialSummary?.Availability ?? "Missing"),
            EscapeCsvValue(report.FinancialSummary?.Finality ?? "Provisional"),
            EscapeCsvValue(report.FinancialSummary?.LatestDataAsOf ?? ""),
            EscapeCsvValue(string.Join("|", report.FinancialSummary?.Sources ?? [])),
            report.Summary.ObservedCostTotal?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            EscapeCsvValue(rec.SavingsStatus)
        };

        return string.Join(",", values);
    }

    /// <summary>
    /// Escapa valores para formato CSV (aspas e vírgulas)
    /// </summary>
    private string EscapeCsvValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        // Avoid spreadsheet formula execution when reports are opened in Excel/LibreOffice.
        if (value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        // Se contém vírgula, quebra de linha ou aspas, precisa ser escapado
        if (value.Contains(',') || value.Contains('\n') || value.Contains('\r') || value.Contains('"'))
        {
            // Substituir aspas duplas por duas aspas duplas e envolver em aspas
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
