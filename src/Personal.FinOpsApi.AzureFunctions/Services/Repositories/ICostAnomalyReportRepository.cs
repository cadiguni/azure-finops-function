using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories;

public interface ICostAnomalyReportRepository
{
    Task SaveReportAsync(CostAnomalyReport report, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CostAnomalyReport>> LoadReportsAsync(DateTime baseDate, int days, CancellationToken cancellationToken = default);
    Task<CostAnomalyReport?> LoadLatestReportAsync(CancellationToken cancellationToken = default);
}
