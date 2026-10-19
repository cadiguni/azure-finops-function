using Azure;
using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public sealed class GovernanceDataHealthService(
    ICostExportVersionStore versions,
    ICostStorageRepository storage,
    IConfiguration configuration,
    ILogger<GovernanceDataHealthService> logger)
{
    public int DelayDays => Math.Clamp(configuration.GetValue("COST_DATA_DELAY_DAYS", 2), 1, 7);

    public string PublicationMode(string subscriptionId) =>
        CostCollectionRouting.IsCutoverSubscription(configuration, subscriptionId)
            ? CostExportPublicationModes.Cutover : CostExportPublicationModes.Shadow;

    public async Task<DataHealthResponse> GetAsync(string subscriptionId, DateOnly from, DateOnly to,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(subscriptionId, out var subscription)) throw new ArgumentException("Subscription inválida.");
        subscriptionId = subscription.ToString("D");
        if (to < from || to.DayNumber - from.DayNumber >= 31)
            throw new ArgumentException("A janela de saúde deve conter entre 1 e 31 dias.");

        var mode = PublicationMode(subscriptionId);
        var warnings = new List<string>
        {
            "Cobertura mede referências publicadas, não valida conteúdo nem sucesso dos coletores. Falhas históricas não são observáveis nesta fonte.",
            "Saúde de filas (backlog/dead-letter) e timers indisponível: nenhuma fonte operacional real está conectada."
        };
        if (mode == CostExportPublicationModes.Shadow)
            warnings.Add("Exports em Shadow: evidência não promovida a custo oficial.");
        CostExportRevisionIndex? index = null;
        CostExportPublicationWatermark? watermark = null;
        var readFailed = false;
        try { index = await versions.LoadIndexAsync(subscriptionId, mode, cancellationToken); }
        catch (Exception ex) when (IsEvidenceFailure(ex))
        {
            logger.LogWarning(ex, "Falha ao ler índice de saúde de {SubscriptionId}", subscriptionId);
            warnings.Add("Índice indisponível; cobertura e gaps não puderam ser observados.");
            readFailed = true;
        }
        try { watermark = await storage.LoadPublicationWatermarkAsync(subscriptionId, mode, cancellationToken); }
        catch (Exception ex) when (IsEvidenceFailure(ex))
        {
            logger.LogWarning(ex, "Falha ao ler watermark de {SubscriptionId}", subscriptionId);
            warnings.Add("Watermark indisponível; a última publicação não pôde ser consultada.");
            readFailed = true;
        }

        var expected = Dates(from, to).ToArray();
        var available = index is null ? null : expected.Where(index.Days.ContainsKey).ToArray();
        var missing = index is null ? null : expected.Where(day => !index.Days.ContainsKey(day)).ToArray();
        var indexedDates = index?.Days.Keys.Select(ParseDay).Where(day => day.HasValue).Select(day => day!.Value).ToArray();
        DateOnly? latest = indexedDates is { Length: > 0 } ? indexedDates.Max() : null;
        // Legacy watermark ranges do not prove that intermediate days exist.
        latest ??= watermark?.MaxUsageDate is { } usage ? DateOnly.FromDateTime(usage) : null;
        DateTimeOffset? lastImport = index?.Days.Values.Select(day => (DateTimeOffset?)day.ImportedAt).Max();
        if (watermark is not null && watermark.ProcessedAt > (lastImport ?? DateTimeOffset.MinValue))
            lastImport = watermark.ProcessedAt;
        if (lastImport == DateTimeOffset.MinValue) lastImport = null;
        bool? stale = latest is null ? null : latest < DateOnly.FromDateTime(now.UtcDateTime).AddDays(-DelayDays);
        if (index is null) warnings.Add("Sem índice versionado: ausência não significa zero nem falha de coleta.");
        var status = readFailed ? "unavailable" : index is null ? "unknown"
            : watermark is not null && !string.Equals(watermark.Status, "Completed", StringComparison.OrdinalIgnoreCase) ? "degraded"
            : missing!.Length > 0 || stale == true ? "incomplete" : "covered";
        return new(subscriptionId, mode, from, to, now, status, lastImport, latest, stale,
            available is null ? null : Math.Round(100m * available.Length / expected.Length, 2),
            available, missing, watermark?.Status, null, null, warnings);
    }

    internal static IEnumerable<string> Dates(DateOnly from, DateOnly to) =>
        Enumerable.Range(0, Math.Max(0, to.DayNumber - from.DayNumber + 1))
            .Select(offset => from.AddDays(offset).ToString("yyyy-MM-dd"));

    private static DateOnly? ParseDay(string day) => DateOnly.TryParseExact(day, "yyyy-MM-dd", out var parsed) ? parsed : null;

    internal static bool IsEvidenceFailure(Exception ex) =>
        ex is RequestFailedException or IOException or InvalidDataException or System.Text.Json.JsonException;
}