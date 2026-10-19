using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Apura quanto do custo diário tem centro de custo atribuído. Mede por custo, não por contagem:
/// dez recursos baratos sem tag pesam menos que um recurso caro sem dono.
/// </summary>
public class CostAllocationCoverageService
{
    private readonly ICostStorageRepository _costStorageRepository;
    private readonly ICostAllocationCoverageRepository _coverageRepository;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostAllocationCoverageService> _logger;

    public CostAllocationCoverageService(
        ICostStorageRepository costStorageRepository,
        ICostAllocationCoverageRepository coverageRepository,
        IConfiguration configuration,
        ILogger<CostAllocationCoverageService> logger)
    {
        _costStorageRepository = costStorageRepository;
        _coverageRepository = coverageRepository;
        _configuration = configuration;
        _logger = logger;
    }

    public string TagName => _configuration["CostAllocationTagName"] ?? "CentrodeCusto";

    public async Task<CostAllocationCoverageDaily> BuildAsync(
        DateTime date,
        CancellationToken cancellationToken = default)
    {
        var rows = await _costStorageRepository.LoadByResourceAllAsync(date, cancellationToken);
        var allocations = await _costStorageRepository.LoadAllocationAllAsync(date, cancellationToken);
        return BuildCoverage(date, rows, allocations, TagName);
    }

    public async Task<CostAllocationCoverageDaily> GenerateAsync(
        DateTime date,
        IReadOnlyList<CostByResourceRow> rows,
        IReadOnlyList<CostAllocationSummary> allocations,
        CancellationToken cancellationToken = default)
    {
        var coverage = BuildCoverage(date, rows, allocations, TagName);
        await _coverageRepository.UpsertAsync(coverage, cancellationToken);

        var tagged = coverage.Subscriptions.Sum(item => item.TaggedCost);
        var untagged = coverage.Subscriptions.Sum(item => item.UntaggedCost);
        _logger.LogInformation(
            "[COST-COVERAGE] date={date} tag={tag} comTag={tagged:N2} semTag={untagged:N2} assinaturas={count}",
            coverage.Date,
            coverage.TagName,
            tagged,
            untagged,
            coverage.Subscriptions.Count);
        return coverage;
    }

    /// <summary>Parte pura: transforma as linhas do dia na apuração de cobertura.</summary>
    public static CostAllocationCoverageDaily BuildCoverage(
        DateTime date,
        IReadOnlyList<CostByResourceRow> rows,
        IReadOnlyList<CostAllocationSummary> allocations,
        string tagName)
    {
        var currency = ResolveDominantCurrency(rows, allocations);
        var scopedRows = rows
            .Where(row => string.IsNullOrWhiteSpace(row.Currency)
                || row.Currency.Equals(currency, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var allocationBySubscription = allocations
            .Where(item => string.IsNullOrWhiteSpace(item.Currency)
                || item.Currency.Equals(currency, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.SubscriptionId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var subscriptionIds = scopedRows
            .Select(row => row.SubscriptionId ?? string.Empty)
            .Concat(allocationBySubscription.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var subscriptions = subscriptionIds
            .Select(subscriptionId =>
            {
                var subscriptionRows = scopedRows
                    .Where(row => (row.SubscriptionId ?? string.Empty)
                        .Equals(subscriptionId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                // O centro de custo já vem resolvido da importação; linhas antigas caem no texto bruto.
                var withCostCenter = subscriptionRows
                    .Select(row => new
                    {
                        Row = row,
                        CostCenter = string.IsNullOrWhiteSpace(row.CostCenter)
                            ? CostTagParser.TryGetTagValue(row.Tags, tagName)
                            : row.CostCenter
                    })
                    .ToList();

                var tagged = withCostCenter.Where(item => !string.IsNullOrWhiteSpace(item.CostCenter)).ToList();
                var untagged = withCostCenter.Where(item => string.IsNullOrWhiteSpace(item.CostCenter)).ToList();
                allocationBySubscription.TryGetValue(subscriptionId, out var allocationRows);

                return new CostAllocationCoverageSubscription
                {
                    SubscriptionId = subscriptionId,
                    TotalCost = allocationRows is null ? null : allocationRows.Sum(item => item.TotalCost),
                    UnallocatedCost = allocationRows is null ? null : allocationRows.Sum(item => item.UnallocatedCost),
                    ResourceCost = subscriptionRows.Sum(row => row.TotalCost),
                    TaggedCost = tagged.Sum(item => item.Row.TotalCost),
                    UntaggedCost = untagged.Sum(item => item.Row.TotalCost),
                    TaggedResourceCount = tagged.Count,
                    UntaggedResourceCount = untagged.Count,
                    CostCenters = tagged
                        .GroupBy(item => item.CostCenter!, StringComparer.OrdinalIgnoreCase)
                        .Select(group => new CostAllocationCoverageCostCenter
                        {
                            Value = group.Key,
                            TotalCost = group.Sum(item => item.Row.TotalCost),
                            ResourceCount = group.Count()
                        })
                        .OrderByDescending(item => item.TotalCost)
                        .ToList()
                };
            })
            .OrderByDescending(item => item.ResourceCost)
            .ToList();

        return new CostAllocationCoverageDaily
        {
            Id = CostAllocationCoverageDaily.BuildId(date),
            Date = date.ToString("yyyy-MM-dd"),
            Currency = currency,
            TagName = tagName,
            HasCostData = rows.Count > 0,
            GeneratedAt = DateTime.UtcNow,
            Subscriptions = subscriptions
        };
    }

    private static string ResolveDominantCurrency(
        IReadOnlyList<CostByResourceRow> rows,
        IReadOnlyList<CostAllocationSummary> allocations)
    {
        var fromRows = rows
            .GroupBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Sum(row => row.TotalCost))
            .Select(group => group.Key)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fromRows))
        {
            return fromRows;
        }

        return allocations
            .GroupBy(item => item.Currency, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Sum(item => item.TotalCost))
            .Select(group => group.Key)
            .FirstOrDefault() ?? "BRL";
    }
}
