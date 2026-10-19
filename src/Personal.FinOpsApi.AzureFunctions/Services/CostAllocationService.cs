using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Monta o rateio por centro de custo somando os snapshots diários por recurso e aplicando as
/// regras manuais. O total é o custo por recurso do período — não o total faturado da assinatura,
/// que inclui linhas sem recurso identificado (ver <see cref="CostAllocationCoverageService"/>).
/// </summary>
public class CostAllocationService
{
    private readonly ICostStorageRepository _costStorageRepository;
    private readonly CostAllocationRulesStorageService _rulesStorage;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CostAllocationService> _logger;

    public CostAllocationService(
        ICostStorageRepository costStorageRepository,
        CostAllocationRulesStorageService rulesStorage,
        IConfiguration configuration,
        ILogger<CostAllocationService> logger)
    {
        _costStorageRepository = costStorageRepository;
        _rulesStorage = rulesStorage;
        _configuration = configuration;
        _logger = logger;
    }

    public string TagName => _configuration["CostAllocationTagName"] ?? "CentrodeCusto";

    /// <summary>
    /// Assinatura de produção usada como escopo padrão. O rateio nasce focado em prod porque é
    /// onde a conta é cobrada de verdade; dev/homolog amplificam o ruído das tags defasadas.
    /// </summary>
    public string DefaultSubscriptionId =>
        _configuration["PRODUCTION_SUBSCRIPTION_ID"] ?? "00000000-0000-0000-0000-00000000a004";

    public async Task<CostAllocationBreakdown> BuildAsync(
        DateTime from,
        DateTime to,
        string? subscriptionId,
        CancellationToken cancellationToken = default)
    {
        var rules = await _rulesStorage.GetRulesAsync(cancellationToken);
        var rows = new List<CostByResourceRow>();
        var daysWithoutData = 0;

        for (var date = from.Date; date <= to.Date; date = date.AddDays(1))
        {
            var dailyRows = subscriptionId is null
                ? await _costStorageRepository.LoadByResourceAllAsync(date, cancellationToken)
                : await _costStorageRepository.LoadByResourceAsync(date, subscriptionId, cancellationToken);
            if (dailyRows.Count == 0)
            {
                daysWithoutData++;
                continue;
            }

            rows.AddRange(dailyRows);
        }

        var breakdown = Build(from, to, subscriptionId, rows, rules, TagName);
        breakdown.DaysWithoutData = daysWithoutData;

        _logger.LogInformation(
            "[COST-ALLOCATION] periodo={from}..{to} assinatura={subscription} total={total:N2} " +
            "atribuido={assigned:N2} naoAtribuido={unassigned:N2} centros={centers}",
            breakdown.From,
            breakdown.To,
            subscriptionId ?? "todas",
            breakdown.TotalCost,
            breakdown.AssignedCost,
            breakdown.UnassignedCost,
            breakdown.CostCenters.Count);

        return breakdown;
    }

    /// <summary>Parte pura: transforma linhas diárias e regras no rateio do período.</summary>
    public static CostAllocationBreakdown Build(
        DateTime from,
        DateTime to,
        string? subscriptionId,
        IReadOnlyList<CostByResourceRow> rows,
        IReadOnlyList<CostAllocationRule> rules,
        string tagName)
    {
        var currency = ResolveDominantCurrency(rows);
        var scopedRows = rows
            .Where(row => string.IsNullOrWhiteSpace(row.Currency)
                || row.Currency.Equals(currency, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Um recurso aparece uma vez por dia do período: soma-se antes de atribuir, para a
        // expansão do centro de custo listar recursos, e não dias.
        var resources = scopedRows
            .GroupBy(row => new
            {
                SubscriptionId = (row.SubscriptionId ?? string.Empty).Trim(),
                ResourceKey = BuildResourceKey(row)
            })
            .Select(group =>
            {
                var reference = group.OrderByDescending(row => row.DataAsOf ?? DateTime.MinValue).First();
                var assignment = CostAllocationResolver.Resolve(reference, rules, tagName);
                return new
                {
                    Assignment = assignment,
                    Item = new CostAllocationResourceItem
                    {
                        ResourceId = reference.ResourceId,
                        Label = string.IsNullOrWhiteSpace(reference.Label) ? reference.ResourceId : reference.Label,
                        ServiceName = reference.ServiceName,
                        ResourceGroup = reference.ResourceGroup,
                        ResourceType = reference.ResourceType,
                        SubscriptionId = group.Key.SubscriptionId,
                        TotalCost = group.Sum(row => row.TotalCost),
                        Currency = currency,
                        Source = assignment.Source,
                        RuleId = assignment.RuleId,
                        TagValue = assignment.RawTagValue,
                        OverridesTag = assignment.IsAssigned
                            && !string.IsNullOrWhiteSpace(assignment.RawTagValue)
                            && !assignment.CostCenter!.Equals(
                                assignment.RawTagValue,
                                StringComparison.OrdinalIgnoreCase)
                    }
                };
            })
            .ToList();

        var totalCost = resources.Sum(entry => entry.Item.TotalCost);
        var assigned = resources.Where(entry => entry.Assignment.IsAssigned).ToList();
        var unassigned = resources.Where(entry => !entry.Assignment.IsAssigned).ToList();
        var assignedCost = assigned.Sum(entry => entry.Item.TotalCost);
        var unassignedCost = unassigned.Sum(entry => entry.Item.TotalCost);

        var costCenters = assigned
            // A chave é normalizada: "TI", "ti" e "TI " são o mesmo centro de custo escrito de
            // três jeitos, e três linhas na tela seriam um erro de leitura, não um fato.
            .GroupBy(entry => NormalizeCostCenterKey(entry.Assignment.CostCenter!), StringComparer.Ordinal)
            .Select(group => new CostAllocationGroup
            {
                CostCenter = ResolveDisplayName(group.Select(entry => entry.Assignment.CostCenter!)),
                Variants = group
                    .Select(entry => entry.Assignment.CostCenter!.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                TotalCost = group.Sum(entry => entry.Item.TotalCost),
                Percent = Percent(group.Sum(entry => entry.Item.TotalCost), totalCost),
                ResourceCount = group.Count(),
                ManualCost = group
                    .Where(entry => IsManual(entry.Assignment.Source))
                    .Sum(entry => entry.Item.TotalCost),
                ResourceGroups = BuildResourceGroups(group.Select(entry => entry.Item))
            })
            .OrderByDescending(group => group.TotalCost)
            .ToList();

        return new CostAllocationBreakdown
        {
            From = from.ToString("yyyy-MM-dd"),
            To = to.ToString("yyyy-MM-dd"),
            Currency = currency,
            TagName = tagName,
            SubscriptionId = subscriptionId,
            SubscriptionIds = resources
                .Select(entry => entry.Item.SubscriptionId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            GeneratedAt = DateTime.UtcNow,
            TotalCost = totalCost,
            AssignedCost = assignedCost,
            UnassignedCost = unassignedCost,
            CoveragePercent = totalCost > 0 ? Percent(assignedCost, totalCost) : null,
            ManualCost = assigned
                .Where(entry => IsManual(entry.Assignment.Source))
                .Sum(entry => entry.Item.TotalCost),
            RulesApplied = assigned
                .Select(entry => entry.Assignment.RuleId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            CostCenters = costCenters,
            Unassigned = new CostAllocationGroup
            {
                CostCenter = "Não atribuído",
                TotalCost = unassignedCost,
                Percent = Percent(unassignedCost, totalCost),
                ResourceCount = unassigned.Count,
                ManualCost = 0m,
                ResourceGroups = BuildResourceGroups(unassigned.Select(entry => entry.Item))
            }
        };
    }

    private static List<CostAllocationResourceGroup> BuildResourceGroups(
        IEnumerable<CostAllocationResourceItem> items) =>
        items
            .GroupBy(item => $"{item.SubscriptionId}|{ResolveResourceGroupName(item)}".ToLowerInvariant())
            .Select(group => new CostAllocationResourceGroup
            {
                ResourceGroup = ResolveResourceGroupName(group.First()),
                SubscriptionId = group.First().SubscriptionId,
                TotalCost = group.Sum(item => item.TotalCost),
                ResourceCount = group.Count(),
                Resources = group.OrderByDescending(item => item.TotalCost).ToList()
            })
            .OrderByDescending(group => group.TotalCost)
            .ToList();

    private static string ResolveResourceGroupName(CostAllocationResourceItem item) =>
        string.IsNullOrWhiteSpace(item.ResourceGroup) ? "(sem resource group)" : item.ResourceGroup.Trim();

    /// <summary>
    /// Chave de agrupamento do centro de custo: sem caixa, sem espaços de borda e sem espaços
    /// repetidos ou invisíveis, que é como as tags divergem sem ninguém perceber.
    /// </summary>
    public static string NormalizeCostCenterKey(string value)
    {
        var builder = new System.Text.StringBuilder((value ?? string.Empty).Length);
        var previousWasSpace = true; // começa em true para descartar os espaços à esquerda
        foreach (var character in value ?? string.Empty)
        {
            // Zero-width space e BOM entram em tag copiada de planilha e não se veem na tela.
            if (character is '\u200b' or '\ufeff')
            {
                continue;
            }

            // NBSP conta como espaço: para quem digitou a tag, ele era um espaço comum.
            var isSpace = char.IsWhiteSpace(character) || character == '\u00a0';
            if (isSpace && previousWasSpace)
            {
                continue;
            }

            builder.Append(isSpace ? ' ' : char.ToUpperInvariant(character));
            previousWasSpace = isSpace;
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Entre grafias concorrentes, exibe a mais usada; empate resolve em ordem alfabética.</summary>
    private static string ResolveDisplayName(IEnumerable<string> values) =>
        values
            .Select(value => value.Trim())
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .First();

    private static bool IsManual(string source) =>
        source is CostAllocationSources.ResourceGroupRule
            or CostAllocationSources.NamePrefixRule
            or CostAllocationSources.ServiceRule
            or CostAllocationSources.TagAlias;

    /// <summary>Linhas sem resourceId ainda precisam de chave: o rótulo evita fundir recursos distintos.</summary>
    private static string BuildResourceKey(CostByResourceRow row)
    {
        var resourceId = (row.ResourceId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(resourceId)
            ? $"label:{(row.Label ?? string.Empty).Trim()}|{(row.ServiceName ?? string.Empty).Trim()}".ToLowerInvariant()
            : resourceId.TrimEnd('/').ToLowerInvariant();
    }

    private static decimal Percent(decimal value, decimal total) =>
        total <= 0 ? 0m : Math.Round(value / total * 100m, 2, MidpointRounding.AwayFromZero);

    private static string ResolveDominantCurrency(IReadOnlyList<CostByResourceRow> rows) =>
        rows
            .Where(row => !string.IsNullOrWhiteSpace(row.Currency))
            .GroupBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Sum(row => row.TotalCost))
            .Select(group => group.Key)
            .FirstOrDefault() ?? "BRL";
}
