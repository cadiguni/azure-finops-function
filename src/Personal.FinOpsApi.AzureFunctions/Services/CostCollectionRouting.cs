using Microsoft.Extensions.Configuration;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public static class CostCollectionRouting
{
    public const string EnableExportImportSetting = "EnableCostExportImport";
    public const string ImportAllExportSubscriptionsSetting = "COST_EXPORT_IMPORT_ALL_SUBSCRIPTIONS";
    public const string ExportSubscriptionsSetting = "COST_EXPORT_SUBSCRIPTIONS";
    public const string ExportCutoverSubscriptionsSetting = "COST_EXPORT_CUTOVER_SUBSCRIPTIONS";
    public const string EnableLegacyByServiceSetting = "EnableLegacyCostByServiceCollection";
    public const string EnableLegacyByResourceSetting = "EnableLegacyCostByResourceCollection";

    public static bool IsLegacyByServiceEnabled(IConfiguration configuration)
    {
        return configuration.GetValue(EnableLegacyByServiceSetting, false);
    }

    public static bool IsLegacyByResourceEnabled(IConfiguration configuration)
    {
        return configuration.GetValue(EnableLegacyByResourceSetting, false);
    }

    public static bool IsCutoverSubscription(IConfiguration configuration, string subscriptionId)
    {
        if (!configuration.GetValue(EnableExportImportSetting, false) ||
            string.IsNullOrWhiteSpace(subscriptionId))
        {
            return false;
        }

        var ingestionIncludesSubscription =
            IsImportAllExportSubscriptionsEnabled(configuration) ||
            ParseAllowlist(configuration, ExportSubscriptionsSetting).Contains(subscriptionId);
        var cutover = ParseAllowlist(configuration, ExportCutoverSubscriptionsSetting);
        return ingestionIncludesSubscription && cutover.Contains(subscriptionId);
    }

    public static bool IsExportIngestionSubscription(
        IConfiguration configuration,
        string subscriptionId)
    {
        return configuration.GetValue(EnableExportImportSetting, false) &&
            !string.IsNullOrWhiteSpace(subscriptionId) &&
            (IsImportAllExportSubscriptionsEnabled(configuration) ||
             ParseAllowlist(configuration, ExportSubscriptionsSetting).Contains(subscriptionId));
    }

    public static bool IsImportAllExportSubscriptionsEnabled(IConfiguration configuration) =>
        configuration.GetValue(ImportAllExportSubscriptionsSetting, false);

    public static void ValidateExportConfiguration(IConfiguration configuration)
    {
        var ingestion = ParseAllowlist(configuration, ExportSubscriptionsSetting);
        var cutover = ParseAllowlist(configuration, ExportCutoverSubscriptionsSetting);
        if (cutover.Count == 0)
        {
            return;
        }

        if (!configuration.GetValue(EnableExportImportSetting, false))
        {
            throw new InvalidOperationException(
                "COST_EXPORT_CUTOVER_SUBSCRIPTIONS exige EnableCostExportImport=true.");
        }

        List<string> subscriptionsOutsideIngestion = IsImportAllExportSubscriptionsEnabled(configuration)
            ? []
            : cutover.Except(ingestion, StringComparer.OrdinalIgnoreCase).ToList();
        if (subscriptionsOutsideIngestion.Count > 0)
        {
            throw new InvalidOperationException(
                "COST_EXPORT_CUTOVER_SUBSCRIPTIONS deve ser subconjunto de COST_EXPORT_SUBSCRIPTIONS. " +
                $"IDs ausentes: {string.Join(",", subscriptionsOutsideIngestion)}.");
        }
    }

    public static List<string> FilterLegacySubscriptions(
        IConfiguration configuration,
        IEnumerable<string> subscriptionIds)
    {
        return subscriptionIds
            .Where(subscriptionId =>
                !string.IsNullOrWhiteSpace(subscriptionId) &&
                !IsCutoverSubscription(configuration, subscriptionId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static HashSet<string> ParseAllowlist(IConfiguration configuration, string settingName)
    {
        return (configuration[settingName] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
