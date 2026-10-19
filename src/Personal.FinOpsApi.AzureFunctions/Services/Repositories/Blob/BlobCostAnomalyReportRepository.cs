using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories.Blob;

public class BlobCostAnomalyReportRepository : ICostAnomalyReportRepository
{
    private readonly CostAnomalyStorageService _storageService;

    public BlobCostAnomalyReportRepository(CostAnomalyStorageService storageService)
    {
        _storageService = storageService;
    }

    public Task SaveReportAsync(CostAnomalyReport report, CancellationToken cancellationToken = default)
        => _storageService.SaveAnomalyReportAsync(report, cancellationToken);

    public async Task<IReadOnlyList<CostAnomalyReport>> LoadReportsAsync(DateTime baseDate, int days, CancellationToken cancellationToken = default)
    {
        var boundedDays = Math.Clamp(days, 1, 30);
        var reports = new List<CostAnomalyReport>(boundedDays);
        for (var i = 0; i < boundedDays; i++)
        {
            var targetDate = baseDate.AddDays(-i).ToString("yyyy-MM-dd");
            var report = await _storageService.LoadAnomalyReportAsync(targetDate, cancellationToken);
            if (report != null)
            {
                reports.Add(report);
            }
        }

        return reports;
    }

    public Task<CostAnomalyReport?> LoadLatestReportAsync(CancellationToken cancellationToken = default)
        => _storageService.LoadLatestAnomalyReportAsync(cancellationToken);
}
