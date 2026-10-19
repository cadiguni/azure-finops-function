using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public static class DashboardSnapshotSelection
{
    public static async Task<(DateTime Date, DashboardSummaryDocument? Summary)> ResolveAsync(
        IDashboardSummaryRepository repository, DateTime requestedDate)
    {
        (DateTime Date, DashboardSummaryDocument? Summary)? unavailable = null;
        for (var offset = 0; offset <= 7; offset++)
        {
            var date = requestedDate.Date.AddDays(-offset);
            var summary = await repository.GetByTypeAndDateAsync(DashboardSummaryTypes.GlobalDailySummary, date);
            if (summary == null) continue;
            if (summary.FinancialSummary?.Availability is "Missing" or "Failed")
            {
                unavailable ??= (date, summary);
                continue;
            }
            return (date, summary);
        }
        return unavailable ?? (requestedDate.Date, null);
    }
}
