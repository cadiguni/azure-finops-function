using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Soma os rollups diários na janela pedida. Trabalha apenas com fato financeiro: não estima
/// economia nem interpreta uso.
/// </summary>
public static class DatabaseCostAggregator
{
    public static CostDatabaseOverview Build(
        IReadOnlyList<CostDatabaseDailyRollup> currentRollups,
        IReadOnlyList<CostDatabaseDailyRollup> previousRollups,
        CostDatabaseRange range)
    {
        var currency = ResolveDominantCurrency(currentRollups, previousRollups);
        var current = currentRollups.Where(rollup => MatchesCurrency(rollup.Currency, currency)).ToList();
        var previous = previousRollups.Where(rollup => MatchesCurrency(rollup.Currency, currency)).ToList();

        var previousByResource = previous
            .SelectMany(rollup => rollup.Resources)
            .GroupBy(resource => ResourceKey(resource), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(resource => resource.TotalCost),
                StringComparer.OrdinalIgnoreCase);

        var resources = current
            .SelectMany(rollup => rollup.Resources.Select(resource => (rollup.Date, Resource: resource)))
            .GroupBy(entry => ResourceKey(entry.Resource), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var sample = group.First().Resource;
                return new CostDatabaseResourceTotal
                {
                    ResourceId = sample.ResourceId,
                    Label = sample.Label,
                    Engine = sample.Engine,
                    ResourceGroup = sample.ResourceGroup,
                    ResourceType = sample.ResourceType,
                    SubscriptionId = sample.SubscriptionId,
                    TotalCost = group.Sum(entry => entry.Resource.TotalCost),
                    PreviousTotalCost = previousByResource.TryGetValue(group.Key, out var previousCost)
                        ? previousCost
                        : null,
                    DaysWithCost = group.Select(entry => entry.Date).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                };
            })
            .OrderByDescending(resource => resource.TotalCost)
            .ToList();

        var previousResources = previous.SelectMany(rollup => rollup.Resources).ToList();
        var engines = new[] { DatabaseResourceClassifier.SqlEngine, DatabaseResourceClassifier.CosmosEngine }
            .Select(engine => new CostDatabaseEngineTotal
            {
                Engine = engine,
                TotalCost = resources.Where(resource => resource.Engine == engine).Sum(resource => resource.TotalCost),
                PreviousTotalCost = previousResources.Any(resource => resource.Engine == engine)
                    ? previousResources.Where(resource => resource.Engine == engine).Sum(resource => resource.TotalCost)
                    : null,
                ResourceCount = resources.Count(resource => resource.Engine == engine)
            })
            .ToList();

        var daily = current
            .Select(rollup => new CostDatabaseDailyPoint
            {
                Date = rollup.Date,
                SqlCost = rollup.Resources
                    .Where(resource => resource.Engine == DatabaseResourceClassifier.SqlEngine)
                    .Sum(resource => resource.TotalCost),
                CosmosCost = rollup.Resources
                    .Where(resource => resource.Engine == DatabaseResourceClassifier.CosmosEngine)
                    .Sum(resource => resource.TotalCost)
            })
            .OrderBy(point => point.Date, StringComparer.Ordinal)
            .ToList();

        var latest = current.Where(rollup => rollup.HasCostData)
            .OrderByDescending(rollup => rollup.Date, StringComparer.Ordinal)
            .FirstOrDefault();

        return new CostDatabaseOverview
        {
            Range = range,
            DataAsOf = latest?.DataAsOf,
            Source = latest?.Source ?? CostDataSources.CostManagementQueryApi,
            Currency = currency,
            DaysWithoutData = CountExpectedDays(range.From, range.To) - current.Count(rollup => rollup.HasCostData),
            DaysWithData = current.Count(rollup => rollup.HasCostData),
            ExpectedDays = CountExpectedDays(range.From, range.To),
            PreviousDaysWithoutData = CountExpectedDays(range.PreviousFrom, range.PreviousTo)
                - previous.Count(rollup => rollup.HasCostData),
            Engines = engines,
            Subscriptions = BuildSubscriptions(current),
            Resources = resources,
            Daily = daily
        };
    }

    private static List<CostDatabaseSubscriptionTotal> BuildSubscriptions(IReadOnlyList<CostDatabaseDailyRollup> rollups)
    {
        var subscriptionIds = rollups
            .SelectMany(rollup => rollup.Resources.Select(resource => resource.SubscriptionId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return subscriptionIds
            .Select(subscriptionId =>
            {
                var resources = rollups
                    .SelectMany(rollup => rollup.Resources
                        .Where(resource => resource.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase))
                        .Select(resource => (rollup.Date, Resource: resource)))
                    .ToList();
                var daysWithDatabaseCost = resources
                    .Select(entry => entry.Date)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                var allocationDays = rollups
                    .Where(rollup => rollup.SubscriptionTotals.Any(total =>
                        total.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase)
                        && total.SubscriptionTotalCost.HasValue))
                    .ToList();

                // A partial allocation would shrink the denominator and inflate the share, so an
                // incomplete period reports an unknown total instead of a wrong percentage.
                decimal? subscriptionTotal = allocationDays.Count >= daysWithDatabaseCost && allocationDays.Count > 0
                    ? allocationDays.Sum(rollup => rollup.SubscriptionTotals
                        .Where(total => total.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase))
                        .Sum(total => total.SubscriptionTotalCost ?? 0m))
                    : null;
                var totalCost = resources.Sum(entry => entry.Resource.TotalCost);

                return new CostDatabaseSubscriptionTotal
                {
                    SubscriptionId = subscriptionId,
                    TotalCost = totalCost,
                    SqlCost = resources.Where(entry => entry.Resource.Engine == DatabaseResourceClassifier.SqlEngine)
                        .Sum(entry => entry.Resource.TotalCost),
                    CosmosCost = resources.Where(entry => entry.Resource.Engine == DatabaseResourceClassifier.CosmosEngine)
                        .Sum(entry => entry.Resource.TotalCost),
                    SubscriptionTotalCost = subscriptionTotal,
                    ShareOfSubscriptionPercent = subscriptionTotal is > 0
                        ? Math.Round(totalCost / subscriptionTotal.Value * 100m, 2)
                        : null,
                    ResourceCount = resources.Select(entry => ResourceKey(entry.Resource))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count()
                };
            })
            .OrderByDescending(subscription => subscription.TotalCost)
            .ToList();
    }

    private static int CountExpectedDays(string fromText, string toText)
        => DateTime.TryParse(fromText, out var from) && DateTime.TryParse(toText, out var to)
            ? Math.Max(0, (to.Date - from.Date).Days + 1)
            : 0;

    private static string ResolveDominantCurrency(
        IReadOnlyList<CostDatabaseDailyRollup> current,
        IReadOnlyList<CostDatabaseDailyRollup> previous)
    {
        var dominant = current
            .Where(rollup => rollup.Resources.Count > 0)
            .GroupBy(rollup => rollup.Currency, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Sum(rollup => rollup.Resources.Sum(resource => resource.TotalCost)))
            .Select(group => group.Key)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(dominant))
        {
            return dominant;
        }

        return previous
            .Where(rollup => rollup.Resources.Count > 0)
            .GroupBy(rollup => rollup.Currency, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Sum(rollup => rollup.Resources.Sum(resource => resource.TotalCost)))
            .Select(group => group.Key)
            .FirstOrDefault()
            ?? current.Select(rollup => rollup.Currency).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? "BRL";
    }

    private static bool MatchesCurrency(string value, string currency)
        => string.IsNullOrWhiteSpace(value) || value.Equals(currency, StringComparison.OrdinalIgnoreCase);

    private static string ResourceKey(CostDatabaseRollupResource resource)
    {
        var key = string.IsNullOrWhiteSpace(resource.ResourceId) ? resource.Label : resource.ResourceId;
        return key.Trim().TrimEnd('/').ToLowerInvariant();
    }
}
