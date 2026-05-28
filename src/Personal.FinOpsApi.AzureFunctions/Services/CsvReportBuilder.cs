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
        BuildCsvData(csv, report);

        _logger.LogInformation("✅ Relatório CSV gerado: {lines} linhas", csv.ToString().Split('\n').Length);
        return csv.ToString();
    }

    private void BuildCsvHeader(StringBuilder csv)
    {
        csv.AppendLine("ManagementGroup,ManagementGroupName,SubscriptionId,SubscriptionName,ResourceGroup,Location,ResourceName,ResourceType,ResourceId,Action,Priority,Confidence,Description,PotentialSavingsBRL,CurrentCostBRL,AnalysisDate");
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
                        var line = BuildCsvLine(mg, subscription, rg, rec, report.AnalysisDate);
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
        DateTime analysisDate)
    {
        var values = new[]
        {
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
            analysisDate.ToString("yyyy-MM-dd")
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

        // Se contém vírgula, quebra de linha ou aspas, precisa ser escapado
        if (value.Contains(',') || value.Contains('\n') || value.Contains('\r') || value.Contains('"'))
        {
            // Substituir aspas duplas por duas aspas duplas e envolver em aspas
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}