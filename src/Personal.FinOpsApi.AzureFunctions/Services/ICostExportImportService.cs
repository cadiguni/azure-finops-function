using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public interface ICostExportImportService
{
    Task<CostExportImportResult> ImportLatestAsync(
        CancellationToken cancellationToken = default);
}
