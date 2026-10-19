using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Consolida, uma vez por dia, o custo observado de SQL e Cosmos DB a partir do snapshot por
/// recurso. O custo de varrer os blobs fica no timer, e não em cada requisição do portal.
/// </summary>
public class DatabaseCostRollupService
{
    private readonly ICostStorageRepository _costStorageRepository;
    private readonly IDatabaseCostRollupRepository _rollupRepository;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseCostRollupService> _logger;

    public DatabaseCostRollupService(
        ICostStorageRepository costStorageRepository,
        IDatabaseCostRollupRepository rollupRepository,
        IConfiguration configuration,
        ILogger<DatabaseCostRollupService> logger)
    {
        _costStorageRepository = costStorageRepository;
        _rollupRepository = rollupRepository;
        _configuration = configuration;
        _logger = logger;
    }

    public DateTime ResolveDefaultRollupDate()
    {
        var delay = Math.Clamp(_configuration.GetValue<int?>("COST_DATA_DELAY_DAYS") ?? 2, 1, 7);
        return DateTime.UtcNow.Date.AddDays(-delay);
    }

    public async Task<CostDatabaseDailyRollup> BuildAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var rows = await _costStorageRepository.LoadByResourceAllAsync(date, cancellationToken);
        var allocations = await _costStorageRepository.LoadAllocationAllAsync(date, cancellationToken);
        return BuildRollup(date, rows, allocations);
    }

    public async Task<CostDatabaseDailyRollup> GenerateAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var rows = await _costStorageRepository.LoadByResourceAllAsync(date, cancellationToken);
        var allocations = await _costStorageRepository.LoadAllocationAllAsync(date, cancellationToken);
        return await GenerateAsync(date, rows, allocations, cancellationToken);
    }

    /// <summary>
    /// Sobrecarga para quem já carregou os snapshots do dia, evitando reler os mesmos blobs.
    /// </summary>
    public async Task<CostDatabaseDailyRollup> GenerateAsync(
        DateTime date,
        IReadOnlyList<CostByResourceRow> rows,
        IReadOnlyList<CostAllocationSummary> allocations,
        CancellationToken cancellationToken = default)
    {
        var rollup = BuildRollup(date, rows, allocations);
        await _rollupRepository.UpsertAsync(rollup, cancellationToken);
        _logger.LogInformation(
            "[DB-COST-ROLLUP] date={date} recursos={resources} assinaturas={subscriptions} moeda={currency} temDados={hasData}",
            rollup.Date,
            rollup.Resources.Count,
            rollup.SubscriptionTotals.Count,
            rollup.Currency,
            rollup.HasCostData);
        return rollup;
    }

    /// <summary>Parte pura: transforma as linhas do dia no documento consolidado.</summary>
    public static CostDatabaseDailyRollup BuildRollup(
        DateTime date,
        IReadOnlyList<CostByResourceRow> rows,
        IReadOnlyList<CostAllocationSummary> allocations)
    {
        var databaseRows = rows
            .Where(row => DatabaseResourceClassifier.ResolveEngine(row.ResourceType, row.ServiceName) is not null)
            .ToList();
        var currency = databaseRows
            .GroupBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Sum(row => row.TotalCost))
            .Select(group => group.Key)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(currency))
        {
            currency = allocations
                .GroupBy(item => item.Currency, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Sum(item => item.TotalCost))
                .Select(group => group.Key)
                .FirstOrDefault() ?? "BRL";
        }

        var resources = databaseRows
            .Where(row => MatchesCurrency(row.Currency, currency))
            .GroupBy(ResourceKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var sample = group.First();
                return new CostDatabaseRollupResource
                {
                    ResourceId = sample.ResourceId,
                    Label = string.IsNullOrWhiteSpace(sample.Label) ? sample.ResourceId : sample.Label,
                    Engine = DatabaseResourceClassifier.ResolveEngine(sample.ResourceType, sample.ServiceName) ?? string.Empty,
                    ResourceGroup = sample.ResourceGroup,
                    ResourceType = sample.ResourceType,
                    SubscriptionId = sample.SubscriptionId ?? string.Empty,
                    TotalCost = group.Sum(row => row.TotalCost)
                };
            })
            .OrderByDescending(resource => resource.TotalCost)
            .ToList();

        var subscriptionTotals = allocations
            .Where(item => MatchesCurrency(item.Currency, currency))
            .GroupBy(item => item.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CostDatabaseRollupSubscription
            {
                SubscriptionId = group.Key,
                SubscriptionTotalCost = group.Sum(item => item.TotalCost)
            })
            .ToList();

        var source = rows.Count > 0 || allocations.Count > 0
            ? CostDataSources.CostManagementExport
            : CostDataSources.CostManagementQueryApi;

        return new CostDatabaseDailyRollup
        {
            Id = CostDatabaseDailyRollup.BuildId(date),
            Date = date.ToString("yyyy-MM-dd"),
            Currency = currency,
            Source = source,
            DataAsOf = rows.Count > 0 ? date : null,
            HasCostData = rows.Count > 0,
            GeneratedAt = DateTime.UtcNow,
            Resources = resources,
            SubscriptionTotals = subscriptionTotals
        };
    }

    private static bool MatchesCurrency(string value, string currency)
        => string.IsNullOrWhiteSpace(value) || value.Equals(currency, StringComparison.OrdinalIgnoreCase);

    private static string ResourceKey(CostByResourceRow row)
    {
        var key = string.IsNullOrWhiteSpace(row.ResourceId) ? row.Label : row.ResourceId;
        return key.Trim().TrimEnd('/').ToLowerInvariant();
    }
}
