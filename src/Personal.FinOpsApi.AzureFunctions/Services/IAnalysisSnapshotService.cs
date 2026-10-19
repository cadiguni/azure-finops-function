using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public interface IAnalysisSnapshotService
{
    Task<List<CostRecommendation>> GetDailyAnalysisAsync(DateTime date);
}
