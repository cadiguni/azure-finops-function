using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories;

public interface IAuditLogRepository
{
    Task AppendAsync(AuditLogDocument document, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditLogDocument>> GetByEntityIdAsync(string entityId, int limit = 100, CancellationToken cancellationToken = default);
}
