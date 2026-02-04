namespace Gvdasa.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// 🎯 CENTRALIZADOR DE PATHS - FASE B ATUALIZADA
/// Responsável por gerar paths consistentes para Blob Storage
/// Estrutura separada:
/// - analyses/: raw-analysis.json e recommendations.json
/// - summaries/: summary.json
/// </summary>
public static class BlobPathBuilder
{
    /// <summary>
    /// Gera path para análises (raw + recommendations)
    /// Padrão: analyses/year=YYYY/month=MM/day=DD/subscription=XXXX/arquivo.json
    /// </summary>
    public static string BuildAnalysisPath(
        DateTime date,
        string subscriptionId,
        string fileName)
    {
        return $"analyses/year={date:yyyy}/" +
               $"month={date:MM}/" +
               $"day={date:dd}/" +
               $"subscription={subscriptionId}/" +
               fileName;
    }

    /// <summary>
    /// Gera path para summaries
    /// Padrão: summaries/year=YYYY/month=MM/day=DD/subscription=XXXX/arquivo.json
    /// </summary>
    public static string BuildSummaryPath(
        DateTime date,
        string subscriptionId,
        string fileName)
    {
        return $"summaries/year={date:yyyy}/" +
               $"month={date:MM}/" +
               $"day={date:dd}/" +
               $"subscription={subscriptionId}/" +
               fileName;
    }

    /// <summary>
    /// Gera prefixo para buscar análises de um dia
    /// </summary>
    public static string BuildAnalysesDailyPrefix(DateTime date)
    {
        return $"analyses/year={date:yyyy}/" +
               $"month={date:MM}/" +
               $"day={date:dd}/";
    }

    /// <summary>
    /// Gera path para summary consolidado diário (sem subscription)
    /// </summary>
    public static string BuildDailySummaryPath(DateTime date)
    {
        return $"summaries/year={date:yyyy}/" +
               $"month={date:MM}/" +
               $"day={date:dd}/" +
               "summary.json";
    }

    /// <summary>
    /// Gera path para top 10 diário (sem subscription)  
    /// </summary>
    public static string BuildDailyTop10Path(DateTime date)
    {
        return $"summaries/year={date:yyyy}/" +
               $"month={date:MM}/" +
               $"day={date:dd}/" +
               "top10.json";
    }

    /// <summary>
    /// Padroniza nomes de arquivos FinOps
    /// </summary>
    public static class FileNames
    {
        public const string Recommendations = "recommendations.json";
        public const string Summary = "summary.json";
        public const string RawAnalysis = "raw-analysis.json";
        
        /// <summary>
        /// Gera nome temporário com analysisId (para debug/auditoria)
        /// </summary>
        public static string WithAnalysisId(string analysisId) => $"analysisId={analysisId}.json";
    }
}