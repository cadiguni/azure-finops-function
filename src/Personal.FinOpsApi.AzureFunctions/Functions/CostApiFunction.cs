using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>APIs genéricas de leitura dos snapshots de custo usados pelo portal.</summary>
public sealed class CostApiFunction
{
    private const int MaximumTrendDays = 366;
    private const int MaximumDatabaseRangeDays = 92;

    /// <summary>Teto de rollups reconstruídos numa requisição, para o backfill não virar varredura.</summary>
    private const int MaximumOnDemandRollupDays = 7;
    private const int DefaultResourceSnapshotLookbackDays = 90;

    /// <summary>
    /// Quantos dias a busca continua depois de achar o snapshot mais recente, para alcançar
    /// assinaturas que publicam com atraso sem varrer a janela inteira.
    /// </summary>
    // A small default keeps the listing responsive: each extra day means another full snapshot
    // read. Environments that intentionally publish farther apart can raise this setting.
    private const int DefaultResourceSnapshotStaleSpreadDays = 2;

    private readonly ICostStorageRepository _repository;
    private readonly ICostExportVersionStore _costExportVersions;
    private readonly IDatabaseCostRollupRepository _databaseCostRollupRepository;
    private readonly DatabaseCostRollupService _databaseCostRollupService;
    private readonly ICostAllocationCoverageRepository _coverageRepository;
    private readonly CostAllocationCoverageService _coverageService;
    private readonly IConfiguration _configuration;
    private readonly ICurrentUserService _currentUserService;
    private readonly AuthorizationService _authorizationService;
    private readonly ILogger<CostApiFunction> _logger;

    public CostApiFunction(
        ICostStorageRepository repository,
        ICostExportVersionStore costExportVersions,
        IDatabaseCostRollupRepository databaseCostRollupRepository,
        DatabaseCostRollupService databaseCostRollupService,
        ICostAllocationCoverageRepository coverageRepository,
        CostAllocationCoverageService coverageService,
        IConfiguration configuration,
        ICurrentUserService currentUserService,
        AuthorizationService authorizationService,
        ILogger<CostApiFunction> logger)
    {
        _repository = repository;
        _costExportVersions = costExportVersions;
        _databaseCostRollupRepository = databaseCostRollupRepository;
        _databaseCostRollupService = databaseCostRollupService;
        _coverageRepository = coverageRepository;
        _coverageService = coverageService;
        _configuration = configuration;
        _currentUserService = currentUserService;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    [Function("CostHealth")]
    public async Task<HttpResponseData> HealthCheckAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost/health")] HttpRequestData req)
    {
        var denied = await RequireReaderAsync(req);
        if (denied is not null)
        {
            return denied;
        }

        var date = ResolveDefaultCostDate();
        var storageAccess = await _repository.CanAccessStorageAsync();
        var dataAvailable = await _repository.ExistsByServiceDataAsync(date);
        var response = req.CreateResponse(storageAccess && dataAvailable
            ? HttpStatusCode.OK
            : HttpStatusCode.ServiceUnavailable);
        await response.WriteAsJsonAsync(new
        {
            status = storageAccess && dataAvailable ? "ok" : "error",
            timestamp = DateTime.UtcNow,
            checks = new
            {
                storageAccess,
                byServiceDataAvailable = dataAvailable,
                date = date.ToString("yyyy-MM-dd")
            }
        });
        return response;
    }

    [Function("CostByService")]
    public async Task<HttpResponseData> GetCostByServiceAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost/by-service")] HttpRequestData req)
    {
        var denied = await RequireReaderAsync(req);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            var date = ParseDateOrDefault(req.Query?["date"], ResolveDefaultCostDate());
            var subscription = NormalizeFilter(req.Query?["subscription"]);
            var service = NormalizeFilter(Decode(req.Query?["service"]));
            var currency = NormalizeFilter(req.Query?["currency"]);
            var rows = await LoadByServiceAsync(date, subscription);
            var subscriptionIds = ResolveRequestedSubscriptions(
                subscription,
                rows.Select(row => row.SubscriptionId));

            var items = rows
                .Where(row => Matches(row.Label, service) && MatchesExact(row.Currency, currency))
                .GroupBy(row => new { row.Label, row.Currency })
                .Select(group => new CostByServiceRow
                {
                    Label = group.Key.Label,
                    Currency = group.Key.Currency,
                    TotalCost = group.Sum(row => row.TotalCost),
                    Count = group.Sum(row => Math.Max(1, row.Count)),
                    SubscriptionId = ResolveSingle(group.Select(row => row.SubscriptionId))
                })
                .OrderByDescending(row => row.TotalCost)
                .ToList();
            var allocation = CompleteAllocationOrEmpty(
                    await LoadAllocationAsync(date, subscription),
                    subscriptionIds)
                .Where(item => MatchesExact(item.Currency, currency))
                .ToList();
            var metadata = await ResolveMetadataAsync(date, subscriptionIds);
            return await WriteOkAsync(req, new CostApiResponse<CostByServiceRow>
            {
                DataAsOf = metadata.DataAsOf,
                Source = metadata.Source,
                Items = items,
                Allocation = allocation
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Erro no endpoint CostByService");
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao consultar custos por serviço.");
        }
    }

    [Function("CostTrendByService")]
    public async Task<HttpResponseData> GetCostTrendByServiceAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost/trend-by-service")] HttpRequestData req)
    {
        var denied = await RequireReaderAsync(req);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            var range = ResolveRange(req);
            if (range.Error is not null)
            {
                return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", range.Error);
            }

            var subscription = NormalizeFilter(req.Query?["subscription"]);
            var service = NormalizeFilter(Decode(req.Query?["service"]));
            var currency = NormalizeFilter(req.Query?["currency"]);
            var items = new List<CostByServiceTrendRow>();
            var sourceByDate = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var availableDates = new List<string>();
            var missingDates = new List<string>();
            var versionedIndexes = subscription is null
                ? []
                : await LoadVersionedExportIndexesAsync(subscription);

            for (var date = range.From; date <= range.To; date = date.AddDays(1))
            {
                var day = await LoadHistoricalByServiceDayAsync(
                    date,
                    subscription,
                    versionedIndexes);
                var dateKey = date.ToString("yyyy-MM-dd");
                if (!day.IsAvailable)
                {
                    missingDates.Add(dateKey);
                    continue;
                }

                availableDates.Add(dateKey);
                sourceByDate[dateKey] = day.Source;
                var rows = day.Rows;
                items.AddRange(rows
                    .Where(row => Matches(row.Label, service) && MatchesExact(row.Currency, currency))
                    .GroupBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new CostByServiceTrendRow
                    {
                        Date = date.ToString("yyyy-MM-dd"),
                        Currency = group.Key,
                        TotalCost = group.Sum(row => row.TotalCost)
                    }));
            }

            var sources = sourceByDate.Values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return await WriteOkAsync(req, new CostApiResponse<CostByServiceTrendRow>
            {
                DataAsOf = availableDates.Count == 0
                    ? null
                    : DateTime.ParseExact(
                        availableDates[^1],
                        "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture),
                Source = sources.Count switch
                {
                    0 => "Unknown",
                    1 => sources[0],
                    _ => "Mixed"
                },
                Items = items.OrderBy(item => item.Date).ThenBy(item => item.Currency).ToList(),
                SourceByDate = sourceByDate,
                AvailableDates = availableDates,
                MissingDates = missingDates
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Erro no endpoint CostTrendByService");
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao consultar tendência por serviço.");
        }
    }

    [Function("CostByResource")]
    public async Task<HttpResponseData> GetCostByResourceAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost/by-resource")] HttpRequestData req)
    {
        var denied = await RequireReaderAsync(req);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            var requestedDate = ParseDateOrDefault(req.Query?["date"], ResolveDefaultCostDate());
            var subscription = NormalizeFilter(req.Query?["subscription"]);
            var (date, rows) = await LoadLatestByResourceSnapshotAsync(requestedDate, subscription);
            var subscriptionIds = ResolveRequestedSubscriptions(
                subscription,
                rows.Select(row => row.SubscriptionId));
            var metadata = await ResolveMetadataAsync(date, subscriptionIds);
            var allItems = ApplyResourceFilters(rows, req)
                .GroupBy(row => new
                {
                    row.SubscriptionId,
                    row.ResourceId,
                    row.Label,
                    row.ServiceName,
                    row.ResourceGroup,
                    row.ResourceLocation,
                    row.ResourceType,
                    row.Tags,
                    row.Currency
                })
                .Select(group => new CostByResourceRow
                {
                    ResourceId = group.Key.ResourceId,
                    Label = group.Key.Label,
                    ServiceName = group.Key.ServiceName,
                    ResourceGroup = group.Key.ResourceGroup,
                    ResourceLocation = group.Key.ResourceLocation,
                    ResourceType = group.Key.ResourceType,
                    Tags = group.Key.Tags,
                    Currency = group.Key.Currency,
                    TotalCost = group.Sum(row => row.TotalCost),
                    Count = group.Sum(row => Math.Max(1, row.Count)),
                    SubscriptionId = group.Key.SubscriptionId,
                    DataAsOf = group.Max(row => row.DataAsOf ?? date),
                    Source = metadata.Source,
                    IsStale = group.Any(row => (row.DataAsOf ?? date).Date != requestedDate.Date)
                })
                .OrderByDescending(row => row.TotalCost)
                .ToList();
            var (page, pageSize) = ResolvePagination(req);
            var items = allItems.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            var currency = NormalizeFilter(req.Query?["currency"]);
            var allocation = CompleteAllocationOrEmpty(
                    await LoadAllocationAsync(date, subscription),
                    subscriptionIds)
                .Where(item => MatchesExact(item.Currency, currency))
                .ToList();
            return await WriteOkAsync(req, new CostApiResponse<CostByResourceRow>
            {
                DataAsOf = metadata.DataAsOf,
                Source = metadata.Source,
                Items = items,
                Allocation = allocation,
                RequestedDate = requestedDate,
                TotalCount = allItems.Count,
                Page = page,
                PageSize = pageSize
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Erro no endpoint CostByResource");
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao consultar custos por recurso.");
        }
    }

    /// <summary>
    /// Mesma base do endpoint por recurso, somada por resource group. A grade abre com dezenas de
    /// linhas em vez de milhares e busca o detalhe só quando alguém expande.
    /// </summary>
    [Function("CostByResourceGroup")]
    public async Task<HttpResponseData> GetCostByResourceGroupAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost/by-resource-group")] HttpRequestData req)
    {
        var denied = await RequireReaderAsync(req);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            var requestedDate = ParseDateOrDefault(req.Query?["date"], ResolveDefaultCostDate());
            var subscription = NormalizeFilter(req.Query?["subscription"]);
            var (date, rows) = await LoadLatestByResourceSnapshotAsync(requestedDate, subscription);
            var subscriptionIds = ResolveRequestedSubscriptions(
                subscription,
                rows.Select(row => row.SubscriptionId));
            var metadata = await ResolveMetadataAsync(date, subscriptionIds);
            var currencyFilter = NormalizeFilter(req.Query?["currency"]);
            var minimumCost = ParseDecimalFilter(req.Query?["minCost"]);

            var groups = ApplyResourceFilters(rows, req)
                .GroupBy(row => new
                {
                    SubscriptionId = (row.SubscriptionId ?? string.Empty).Trim(),
                    ResourceGroup = string.IsNullOrWhiteSpace(row.ResourceGroup)
                        ? "(sem resource group)"
                        : row.ResourceGroup.Trim(),
                    row.Currency
                })
                .Select(group => new CostByResourceGroupRow
                {
                    ResourceGroup = group.Key.ResourceGroup,
                    SubscriptionId = group.Key.SubscriptionId,
                    Currency = group.Key.Currency,
                    TotalCost = group.Sum(row => row.TotalCost),
                    // Recursos distintos, não linhas: o mesmo recurso aparece uma vez por medidor.
                    ResourceCount = group
                        .Select(row => string.IsNullOrWhiteSpace(row.ResourceId) ? row.Label : row.ResourceId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    ServiceCount = group
                        .Select(row => row.ServiceName)
                        .WhereNotBlank()
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    TopService = group
                        .GroupBy(row => row.ServiceName, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(service => service.Sum(row => row.TotalCost))
                        .Select(service => service.Key)
                        .FirstOrDefault() ?? string.Empty,
                    DataAsOf = group.Max(row => row.DataAsOf ?? date),
                    IsStale = group.Any(row => (row.DataAsOf ?? date).Date != requestedDate.Date)
                })
                .Where(group => MatchesExact(group.Currency, currencyFilter))
                .Where(group => minimumCost is null || group.TotalCost >= minimumCost.Value)
                .OrderByDescending(group => group.TotalCost)
                .ToList();
            var (page, pageSize) = ResolvePagination(req);
            var items = groups.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            var allocation = CompleteAllocationOrEmpty(
                    await LoadAllocationAsync(date, subscription),
                    subscriptionIds)
                .Where(item => MatchesExact(item.Currency, currencyFilter))
                .ToList();

            return await WriteOkAsync(req, new CostApiResponse<CostByResourceGroupRow>
            {
                DataAsOf = metadata.DataAsOf,
                Source = metadata.Source,
                Items = items,
                Allocation = allocation,
                RequestedDate = requestedDate,
                TotalCount = groups.Count,
                Page = page,
                PageSize = pageSize
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Erro no endpoint CostByResourceGroup");
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao consultar custos por resource group.");
        }
    }

    [Function("CostTrendByResource")]
    public async Task<HttpResponseData> GetCostTrendByResourceAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost/trend-by-resource")] HttpRequestData req)
    {
        var denied = await RequireReaderAsync(req);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            var range = ResolveRange(req);
            if (range.Error is not null)
            {
                return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", range.Error);
            }

            var subscription = NormalizeFilter(req.Query?["subscription"]);
            var items = new List<CostByResourceTrendRow>();
            var subscriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var date = range.From; date <= range.To; date = date.AddDays(1))
            {
                var rows = await LoadByResourceAsync(date, subscription);
                subscriptions.UnionWith(rows.Select(row => row.SubscriptionId).WhereNotBlank());
                items.AddRange(ApplyResourceFilters(rows, req)
                    .GroupBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new CostByResourceTrendRow
                    {
                        Date = date.ToString("yyyy-MM-dd"),
                        Currency = group.Key,
                        TotalCost = group.Sum(row => row.TotalCost)
                    }));
            }

            var metadata = await ResolveMetadataAsync(range.To, subscriptions);
            return await WriteOkAsync(req, new CostApiResponse<CostByResourceTrendRow>
            {
                DataAsOf = metadata.DataAsOf,
                Source = metadata.Source,
                Items = items.OrderBy(item => item.Date).ThenBy(item => item.Currency).ToList()
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Erro no endpoint CostTrendByResource");
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao consultar tendência por recurso.");
        }
    }

    /// <summary>
    /// Retrato financeiro de SQL e Cosmos DB no período: custo observado por engine, assinatura,
    /// recurso e dia. Não estima economia nem lê métricas de uso.
    /// </summary>
    [Function("CostDatabases")]
    public async Task<HttpResponseData> GetCostDatabasesAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost/databases")] HttpRequestData req)
    {
        var denied = await RequireReaderAsync(req);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            var range = ResolveDatabaseRange(req);
            if (range.Error is not null)
            {
                return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", range.Error);
            }

            var subscription = NormalizeFilter(req.Query?["subscription"]);
            var windowDays = (range.To - range.From).Days + 1;
            var previousTo = range.From.AddDays(-1);
            var previousFrom = previousTo.AddDays(-(windowDays - 1));

            // Reads the rollups the timer already consolidated; only the days still missing are
            // rebuilt here, and never more than a handful per request.
            var backfillBudget = MaximumOnDemandRollupDays;
            var current = await LoadRollupsAsync(range.From, range.To, () => backfillBudget--);
            var previous = await LoadRollupsAsync(previousFrom, previousTo, () => backfillBudget--);

            var overview = DatabaseCostAggregator.Build(
                FilterBySubscription(current, subscription),
                FilterBySubscription(previous, subscription),
                new CostDatabaseRange
                {
                    From = range.From.ToString("yyyy-MM-dd"),
                    To = range.To.ToString("yyyy-MM-dd"),
                    PreviousFrom = previousFrom.ToString("yyyy-MM-dd"),
                    PreviousTo = previousTo.ToString("yyyy-MM-dd")
                });

            if (overview.Resources.Count == 0)
            {
                var latestStoredSnapshot = current
                    .Where(rollup => rollup.HasCostData)
                    .OrderByDescending(rollup => rollup.Date, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (latestStoredSnapshot is not null &&
                    DateTime.TryParse(latestStoredSnapshot.Date, out var latestDate))
                {
                    var rebuilt = await _databaseCostRollupService.BuildAsync(latestDate);
                    if (rebuilt.Resources.Count > 0)
                    {
                        var snapshotOverview = DatabaseCostAggregator.Build(
                            FilterBySubscription([rebuilt], subscription),
                            [],
                            new CostDatabaseRange
                            {
                                From = range.From.ToString("yyyy-MM-dd"),
                                To = range.To.ToString("yyyy-MM-dd"),
                                PreviousFrom = previousFrom.ToString("yyyy-MM-dd"),
                                PreviousTo = previousTo.ToString("yyyy-MM-dd")
                            });
                        overview = new CostDatabaseOverview
                        {
                            Range = snapshotOverview.Range,
                            DataAsOf = snapshotOverview.DataAsOf,
                            Source = snapshotOverview.Source,
                            Currency = snapshotOverview.Currency,
                            DaysWithoutData = snapshotOverview.DaysWithoutData,
                            IsLatestSnapshotFallback = true,
                            Engines = snapshotOverview.Engines,
                            Subscriptions = snapshotOverview.Subscriptions,
                            Resources = snapshotOverview.Resources,
                            Daily = snapshotOverview.Daily
                        };
                    }
                }
            }
            return await WriteOkAsync(req, overview);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Erro no endpoint CostDatabases");
            return await ApiHttpResponses.InternalServerError(req, "Erro interno ao consultar custos de bancos de dados.");
        }
    }

    private async Task<List<CostDatabaseDailyRollup>> LoadRollupsAsync(
        DateTime from,
        DateTime to,
        Func<int> takeBackfillSlot)
    {
        var stored = (await _databaseCostRollupRepository.GetRangeAsync(from, to))
            .ToDictionary(rollup => rollup.Date, StringComparer.OrdinalIgnoreCase);
        var rollups = new List<CostDatabaseDailyRollup>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var key = date.ToString("yyyy-MM-dd");
            if (stored.TryGetValue(key, out var rollup))
            {
                rollups.Add(rollup);
                continue;
            }

            if (takeBackfillSlot() <= 0)
            {
                _logger.LogInformation(
                    "[DB-COST-ROLLUP] Sem rollup para {date} e orçamento de reconstrução esgotado. Execute o timer ou aguarde a próxima consolidação.",
                    key);
                continue;
            }

            rollups.Add(await _databaseCostRollupService.BuildAsync(date));
        }

        return rollups;
    }

    private static List<CostDatabaseDailyRollup> FilterBySubscription(
        IReadOnlyList<CostDatabaseDailyRollup> rollups,
        string? subscriptionId)
    {
        if (subscriptionId is null)
        {
            return rollups.ToList();
        }

        return rollups
            .Select(rollup => new CostDatabaseDailyRollup
            {
                Id = rollup.Id,
                Date = rollup.Date,
                Currency = rollup.Currency,
                Source = rollup.Source,
                DataAsOf = rollup.DataAsOf,
                HasCostData = rollup.HasCostData,
                GeneratedAt = rollup.GeneratedAt,
                Resources = rollup.Resources
                    .Where(resource => resource.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase))
                    .ToList(),
                SubscriptionTotals = rollup.SubscriptionTotals
                    .Where(total => total.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase))
                    .ToList()
            })
            .ToList();
    }

    /// <summary>
    /// Cobertura de centro de custo no período: quanto do custo tem tag válida, quanto não tem, e
    /// quais valores de tag aparecem. Fato de governança, base para qualquer rateio.
    /// </summary>
    [Function("CostAllocationCoverage")]
    public async Task<HttpResponseData> GetCostAllocationCoverageAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost/allocation-coverage")] HttpRequestData req)
    {
        var denied = await RequireReaderAsync(req);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            var range = ResolveDatabaseRange(req);
            if (range.Error is not null)
            {
                return await ApiHttpResponses.WriteErrorAsync(req, HttpStatusCode.BadRequest, "BadRequest", range.Error);
            }

            var subscription = NormalizeFilter(req.Query?["subscription"]);
            var backfillBudget = MaximumOnDemandRollupDays;
            var stored = (await _coverageRepository.GetRangeAsync(range.From, range.To))
                .ToDictionary(item => item.Date, StringComparer.OrdinalIgnoreCase);

            var days = new List<CostAllocationCoverageDaily>();
            for (var date = range.From; date <= range.To; date = date.AddDays(1))
            {
                var key = date.ToString("yyyy-MM-dd");
                if (stored.TryGetValue(key, out var coverage))
                {
                    days.Add(coverage);
                    continue;
                }

                if (backfillBudget-- <= 0)
                {
                    continue;
                }

                days.Add(await _coverageService.BuildAsync(date));
            }

            return await WriteOkAsync(req, BuildCoverageResponse(days, range.From, range.To, subscription));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Erro no endpoint CostAllocationCoverage");
            return await ApiHttpResponses.InternalServerError(
                req,
                "Erro interno ao consultar a cobertura de centro de custo.");
        }
    }

    private static CostAllocationCoverageSummary BuildCoverageResponse(
        IReadOnlyList<CostAllocationCoverageDaily> days,
        DateTime from,
        DateTime to,
        string? subscriptionId)
    {
        var scoped = days
            .Select(day => new
            {
                day.Currency,
                day.TagName,
                day.HasCostData,
                Subscriptions = day.Subscriptions
                    .Where(item => subscriptionId is null
                        || item.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase))
                    .ToList()
            })
            .ToList();

        var subscriptions = scoped
            .SelectMany(day => day.Subscriptions)
            .GroupBy(item => item.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CostAllocationCoverageSubscriptionTotal
            {
                SubscriptionId = group.Key,
                ResourceCost = group.Sum(item => item.ResourceCost),
                TaggedCost = group.Sum(item => item.TaggedCost),
                UntaggedCost = group.Sum(item => item.UntaggedCost),
                // Só soma quando todos os dias publicaram alocação: um período incompleto
                // encolheria o denominador e inflaria a cobertura.
                TotalCost = group.All(item => item.TotalCost.HasValue)
                    ? group.Sum(item => item.TotalCost!.Value)
                    : null,
                UnallocatedCost = group.All(item => item.UnallocatedCost.HasValue)
                    ? group.Sum(item => item.UnallocatedCost!.Value)
                    : null,
                CostCenters = group
                    .SelectMany(item => item.CostCenters)
                    .GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
                    .Select(centers => new CostAllocationCoverageCostCenter
                    {
                        Value = centers.Key,
                        TotalCost = centers.Sum(item => item.TotalCost),
                        ResourceCount = centers.Max(item => item.ResourceCount)
                    })
                    .OrderByDescending(item => item.TotalCost)
                    .ToList()
            })
            .OrderByDescending(item => item.ResourceCost)
            .ToList();

        var taggedTotal = subscriptions.Sum(item => item.TaggedCost);
        var untaggedTotal = subscriptions.Sum(item => item.UntaggedCost);
        var resourceTotal = taggedTotal + untaggedTotal;

        return new CostAllocationCoverageSummary
        {
            From = from.ToString("yyyy-MM-dd"),
            To = to.ToString("yyyy-MM-dd"),
            Currency = scoped.FirstOrDefault(day => day.HasCostData)?.Currency ?? "BRL",
            TagName = scoped.FirstOrDefault()?.TagName ?? string.Empty,
            DaysWithoutData = Math.Max(0, (to.Date - from.Date).Days + 1 - days.Count(day => day.HasCostData)),
            TaggedCost = taggedTotal,
            UntaggedCost = untaggedTotal,
            CoveragePercent = resourceTotal > 0
                ? Math.Round(taggedTotal / resourceTotal * 100m, 2)
                : null,
            Subscriptions = subscriptions
        };
    }

    private (DateTime From, DateTime To, string? Error) ResolveDatabaseRange(HttpRequestData req)
    {
        var defaultTo = ResolveDefaultCostDate();
        var from = ParseDateOrDefault(req.Query?["from"], defaultTo.AddDays(-29));
        var to = ParseDateOrDefault(req.Query?["to"], defaultTo);
        if (to < from)
        {
            (from, to) = (to, from);
        }

        // Each day costs one blob read per subscription, and the comparison doubles it.
        return (to - from).TotalDays >= MaximumDatabaseRangeDays
            ? (from, to, $"O período máximo permitido para bancos é de {MaximumDatabaseRangeDays} dias.")
            : (from, to, null);
    }

    private async Task<HttpResponseData?> RequireReaderAsync(HttpRequestData req)
    {
        var user = await _currentUserService.GetCurrentUserAsync(req);
        if (!user.IsAuthenticated)
        {
            return await ApiHttpResponses.Unauthorized(req);
        }

        return _authorizationService.CanRead(user)
            ? null
            : await ApiHttpResponses.Forbidden(req, "Usuário sem permissão de leitura FinOps.");
    }

    private Task<List<CostByServiceRow>> LoadByServiceAsync(DateTime date, string? subscription) =>
        subscription is null
            ? _repository.LoadByServiceAllAsync(date)
            : _repository.LoadByServiceAsync(date, subscription);

    private async Task<IReadOnlyList<(string Mode, CostExportRevisionIndex Index)>> LoadVersionedExportIndexesAsync(
        string subscriptionId)
    {
        if (!CostCollectionRouting.IsExportIngestionSubscription(_configuration, subscriptionId))
        {
            return [];
        }

        var primaryMode = CostCollectionRouting.IsCutoverSubscription(_configuration, subscriptionId)
            ? CostExportPublicationModes.Cutover
            : CostExportPublicationModes.Shadow;
        var secondaryMode = primaryMode == CostExportPublicationModes.Cutover
            ? CostExportPublicationModes.Shadow
            : CostExportPublicationModes.Cutover;
        var result = new List<(string Mode, CostExportRevisionIndex Index)>();
        foreach (var mode in new[] { primaryMode, secondaryMode })
        {
            var index = await _costExportVersions.LoadIndexAsync(subscriptionId, mode);
            if (index?.Days.Count > 0)
            {
                result.Add((mode, index));
            }
        }

        return result;
    }

    private async Task<HistoricalByServiceDay> LoadHistoricalByServiceDayAsync(
        DateTime date,
        string? subscriptionId,
        IReadOnlyList<(string Mode, CostExportRevisionIndex Index)> versionedIndexes)
    {
        var dateKey = date.ToString("yyyy-MM-dd");
        if (subscriptionId is not null)
        {
            foreach (var (mode, index) in versionedIndexes)
            {
                if (!index.Days.TryGetValue(dateKey, out var reference))
                {
                    continue;
                }

                var snapshot = await _costExportVersions.LoadSnapshotAsync(
                    subscriptionId,
                    mode,
                    reference.Revision);
                if (snapshot.Date.Date != date.Date ||
                    !snapshot.CostBasis.Equals("ActualCost", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Snapshot versionado incompatível para subscriptionId={subscriptionId}, date={dateKey}.");
                }

                var exportRows = snapshot.IsTombstone
                    ? new List<CostByServiceRow>
                    {
                        new()
                        {
                            SubscriptionId = subscriptionId,
                            Currency = snapshot.TombstoneCurrency
                                ?? throw new InvalidDataException($"Tombstone sem moeda para date={dateKey}."),
                            TotalCost = 0m
                        }
                    }
                    : snapshot.ByService.ToList();
                EnsureRowsBelongToSubscription(exportRows, subscriptionId, dateKey);
                return new HistoricalByServiceDay(
                    exportRows,
                    CostDataSources.CostManagementExport,
                    IsAvailable: true);
            }
        }

        // O path oficial contém o histórico persistido pelo coletor legado. Ele é lido
        // somente quando não existe uma revisão de Export e nunca consulta o Azure.
        var legacyRows = await LoadByServiceAsync(date, subscriptionId);
        if (legacyRows.Count == 0)
        {
            return new HistoricalByServiceDay([], "Unknown", IsAvailable: false);
        }

        if (subscriptionId is not null)
        {
            EnsureRowsBelongToSubscription(legacyRows, subscriptionId, dateKey);
        }

        return new HistoricalByServiceDay(
            legacyRows,
            CostDataSources.CostManagementQueryApi,
            IsAvailable: true);
    }

    private static void EnsureRowsBelongToSubscription(
        IEnumerable<CostByServiceRow> rows,
        string subscriptionId,
        string date)
    {
        if (rows.Any(row =>
            !string.IsNullOrWhiteSpace(row.SubscriptionId) &&
            !row.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Snapshot contém outra subscription para subscriptionId={subscriptionId}, date={date}.");
        }
    }

    private sealed record HistoricalByServiceDay(
        IReadOnlyList<CostByServiceRow> Rows,
        string Source,
        bool IsAvailable);

    private Task<List<CostByResourceRow>> LoadByResourceAsync(DateTime date, string? subscription) =>
        subscription is null
            ? _repository.LoadByResourceAllAsync(date)
            : _repository.LoadByResourceAsync(date, subscription);

    private async Task<(DateTime Date, List<CostByResourceRow> Rows)> LoadLatestByResourceSnapshotAsync(
        DateTime requestedDate,
        string? subscription)
    {
        // Cost Management data can arrive late. Reuse a local snapshot rather than returning an
        // empty grid or querying Azure on page access. When all subscriptions are requested,
        // each one can legitimately have a different latest Export date.
        var lookbackDays = Math.Clamp(
            _configuration.GetValue<int?>("COST_RESOURCE_SNAPSHOT_LOOKBACK_DAYS")
                ?? DefaultResourceSnapshotLookbackDays,
            1,
            MaximumTrendDays);
        if (subscription is null)
        {
            // Depois do dia mais recente com dados, o retorno cai rápido: uma assinatura que está
            // semanas atrás das outras já não é dado utilizável, e varrer os 90 dias inteiros
            // custava uma listagem de blobs por dia — dezenas de segundos para abrir a grade.
            var staleSpreadDays = Math.Clamp(
                _configuration.GetValue<int?>("COST_RESOURCE_SNAPSHOT_STALE_SPREAD_DAYS")
                    ?? DefaultResourceSnapshotStaleSpreadDays,
                0,
                lookbackDays);
            var rowsByLatestSubscriptionSnapshot = new List<CostByResourceRow>();
            var resolvedSubscriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int? newestOffsetWithData = null;
            for (var offset = 0; offset <= lookbackDays; offset++)
            {
                if (newestOffsetWithData.HasValue && offset > newestOffsetWithData.Value + staleSpreadDays)
                {
                    break;
                }

                var candidateDate = requestedDate.Date.AddDays(-offset);
                var candidateRows = await LoadByResourceAsync(candidateDate, subscription);
                if (candidateRows.Count > 0)
                {
                    newestOffsetWithData ??= offset;
                }

                foreach (var subscriptionRows in candidateRows.GroupBy(row => row.SubscriptionId, StringComparer.OrdinalIgnoreCase))
                {
                    if (!resolvedSubscriptions.Add(subscriptionRows.Key))
                    {
                        continue;
                    }

                    foreach (var row in subscriptionRows)
                    {
                        row.DataAsOf = candidateDate;
                        row.IsStale = candidateDate != requestedDate.Date;
                        rowsByLatestSubscriptionSnapshot.Add(row);
                    }
                }
            }

            var latestDate = rowsByLatestSubscriptionSnapshot
                .Select(row => row.DataAsOf)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .DefaultIfEmpty(requestedDate.Date)
                .Max();
            return (latestDate, rowsByLatestSubscriptionSnapshot);
        }

        for (var offset = 0; offset <= lookbackDays; offset++)
        {
            var candidateDate = requestedDate.Date.AddDays(-offset);
            var rows = await LoadByResourceAsync(candidateDate, subscription);
            if (rows.Count > 0)
            {
                return (candidateDate, rows);
            }
        }

        return (requestedDate.Date, []);
    }

    private static (int Page, int PageSize) ResolvePagination(HttpRequestData req)
    {
        var page = int.TryParse(req.Query?["page"], out var parsedPage) ? Math.Max(1, parsedPage) : 1;
        var pageSize = int.TryParse(req.Query?["pageSize"], out var parsedPageSize)
            ? Math.Clamp(parsedPageSize, 25, 500)
            : 100;
        return (page, pageSize);
    }

    private Task<List<CostAllocationSummary>> LoadAllocationAsync(DateTime date, string? subscription) =>
        subscription is null
            ? _repository.LoadAllocationAllAsync(date)
            : _repository.LoadAllocationAsync(date, subscription);

    private static IEnumerable<CostByResourceRow> ApplyResourceFilters(
        IEnumerable<CostByResourceRow> rows,
        HttpRequestData req)
    {
        var service = NormalizeFilter(Decode(req.Query?["service"]));
        var resource = NormalizeFilter(Decode(req.Query?["resource"]));
        var resourceGroup = NormalizeFilter(Decode(req.Query?["resourceGroup"]));
        var resourceType = NormalizeFilter(Decode(req.Query?["resourceType"]));
        var location = NormalizeFilter(Decode(req.Query?["location"]));
        var currency = NormalizeFilter(req.Query?["currency"]);
        return rows.Where(row =>
            Matches(row.ServiceName, service) &&
            (resource is null || Matches(row.Label, resource) || Matches(row.ResourceId, resource)) &&
            Matches(row.ResourceGroup, resourceGroup) &&
            Matches(row.ResourceType, resourceType) &&
            Matches(row.ResourceLocation, location) &&
            MatchesExact(row.Currency, currency));
    }

    private async Task<(DateTime DataAsOf, string Source)> ResolveMetadataAsync(
        DateTime fallbackDate,
        IEnumerable<string> subscriptionIds)
    {
        var requestedSubscriptions = subscriptionIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var watermarks = new List<CostExportPublicationWatermark>();
        var missingWatermarks = 0;
        foreach (var subscriptionId in requestedSubscriptions)
        {
            var watermark = await _repository.LoadPublicationWatermarkAsync(
                subscriptionId,
                CostExportPublicationModes.Cutover);
            if (watermark is null && CostCollectionRouting.IsExportIngestionSubscription(_configuration, subscriptionId))
            {
                // Before a subscription enters cutover its validated Export data lives in
                // shadow. The portal must describe that source accurately rather than
                // labelling it as a Query API snapshot.
                watermark = await _repository.LoadPublicationWatermarkAsync(
                    subscriptionId,
                    CostExportPublicationModes.Shadow);
            }
            if (watermark is not null)
            {
                watermarks.Add(watermark);
            }
            else
            {
                missingWatermarks++;
            }
        }

        if (watermarks.Count == 0)
        {
            return (fallbackDate.Date, CostDataSources.CostManagementQueryApi);
        }

        var sources = watermarks.Select(item => item.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dataDates = watermarks.Where(item => item.MaxUsageDate.HasValue)
                .Select(item => item.MaxUsageDate!.Value.Date)
                .ToList();
        if (missingWatermarks > 0)
        {
            dataDates.Add(fallbackDate.Date);
        }

        return (
            dataDates.DefaultIfEmpty(fallbackDate.Date).Min(),
            missingWatermarks == 0 && sources.Count == 1 ? sources[0] : "Mixed");
    }

    private static IReadOnlyList<string> ResolveRequestedSubscriptions(
        string? requestedSubscription,
        IEnumerable<string?> rowSubscriptions)
    {
        return requestedSubscription is not null
            ? [requestedSubscription]
            : rowSubscriptions.WhereNotBlank().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<CostAllocationSummary> CompleteAllocationOrEmpty(
        IReadOnlyList<CostAllocationSummary> allocation,
        IReadOnlyCollection<string> expectedSubscriptions)
    {
        if (expectedSubscriptions.Count == 0)
        {
            return [];
        }

        var actualSubscriptions = allocation
            .Select(item => item.SubscriptionId)
            .WhereNotBlank()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return actualSubscriptions.SetEquals(expectedSubscriptions)
            ? allocation
            : [];
    }

    private (DateTime From, DateTime To, string? Error) ResolveRange(HttpRequestData req)
    {
        var defaultTo = ResolveDefaultCostDate();
        var from = ParseDateOrDefault(req.Query?["from"], defaultTo.AddDays(-6));
        var to = ParseDateOrDefault(req.Query?["to"], defaultTo);
        if (to < from)
        {
            (from, to) = (to, from);
        }

        return (to - from).TotalDays >= MaximumTrendDays
            ? (from, to, $"O período máximo permitido é de {MaximumTrendDays} dias.")
            : (from, to, null);
    }

    private DateTime ResolveDefaultCostDate()
    {
        var delay = Math.Clamp(_configuration.GetValue<int?>("COST_DATA_DELAY_DAYS") ?? 2, 1, 7);
        return DateTime.UtcNow.Date.AddDays(-delay);
    }

    private static DateTime ParseDateOrDefault(string? value, DateTime fallback) =>
        DateTime.TryParseExact(
            value,
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var parsed)
            ? parsed.Date
            : fallback.Date;

    private static string? Decode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? value : Uri.UnescapeDataString(value);

    private static string? NormalizeFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();

    private static decimal? ParseDecimalFilter(string? value) =>
        decimal.TryParse(
            value?.Replace(',', '.'),
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) && parsed > 0
                ? parsed
                : null;

    private static bool Matches(string value, string? filter) =>
        filter is null || (!string.IsNullOrWhiteSpace(value) && value.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesExact(string value, string? filter) =>
        filter is null || value.Equals(filter, StringComparison.OrdinalIgnoreCase);

    private static string? ResolveSingle(IEnumerable<string?> values)
    {
        var distinct = values.WhereNotBlank().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    private static async Task<HttpResponseData> WriteOkAsync<T>(HttpRequestData req, T value)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(value);
        return response;
    }
}

internal static class CostApiEnumerableExtensions
{
    public static IEnumerable<string> WhereNotBlank(this IEnumerable<string?> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim());
}
