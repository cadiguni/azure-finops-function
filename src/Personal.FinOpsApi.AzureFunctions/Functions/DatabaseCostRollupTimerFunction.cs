using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
/// Consolida os rollups diários que o portal lê: custo de bancos e cobertura de centro de custo.
/// Os dois derivam dos mesmos snapshots, então o dia é carregado uma única vez.
/// </summary>
public class DatabaseCostRollupTimerFunction
{
    private const int BackfillWindowDays = 92;
    private const int MaximumBackfillDaysPerRun = 7;

    private readonly ICostStorageRepository _costStorageRepository;
    private readonly IDatabaseCostRollupRepository _databaseCostRollupRepository;
    private readonly DatabaseCostRollupService _rollupService;
    private readonly CostAllocationCoverageService _coverageService;
    private readonly ILogger<DatabaseCostRollupTimerFunction> _logger;

    public DatabaseCostRollupTimerFunction(
        ICostStorageRepository costStorageRepository,
        IDatabaseCostRollupRepository databaseCostRollupRepository,
        DatabaseCostRollupService rollupService,
        CostAllocationCoverageService coverageService,
        ILogger<DatabaseCostRollupTimerFunction> logger)
    {
        _costStorageRepository = costStorageRepository;
        _databaseCostRollupRepository = databaseCostRollupRepository;
        _rollupService = rollupService;
        _coverageService = coverageService;
        _logger = logger;
    }

    [Function("DatabaseCostRollupTimer")]
    public async Task RunAsync([TimerTrigger("%DatabaseCostRollupSchedule%")] TimerInfo timer)
    {
        var latestDate = _rollupService.ResolveDefaultRollupDate();
        var firstDate = latestDate.AddDays(-(BackfillWindowDays - 1));
        var existing = await _databaseCostRollupRepository.GetRangeAsync(firstDate, latestDate);
        var datesToGenerate = new[] { latestDate }
            .Concat(Enumerable.Range(1, BackfillWindowDays - 1)
                .Select(offset => latestDate.AddDays(-offset))
                .Where(date => !existing.Any(rollup =>
                    rollup.Date == date.ToString("yyyy-MM-dd") && rollup.HasCostData))
                .Take(MaximumBackfillDaysPerRun - 1))
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        _logger.LogInformation(
            "[DAILY-ROLLUP] Consolidando {count} dia(s), de {from} a {to}.",
            datesToGenerate.Count,
            datesToGenerate.First().ToString("yyyy-MM-dd"),
            datesToGenerate.Last().ToString("yyyy-MM-dd"));

        foreach (var date in datesToGenerate)
        {
            var rows = await _costStorageRepository.LoadByResourceAllAsync(date);
            var allocations = await _costStorageRepository.LoadAllocationAllAsync(date);
            await _rollupService.GenerateAsync(date, rows, allocations);
            await _coverageService.GenerateAsync(date, rows, allocations);
        }
    }
}
