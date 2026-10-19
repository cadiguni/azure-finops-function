using FunctionHttpRequestData = Microsoft.Azure.Functions.Worker.Http.HttpRequestData;
using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public interface ICurrentUserService
{
    Task<CurrentUserContext> GetCurrentUserAsync(FunctionHttpRequestData req, CancellationToken cancellationToken = default);
}
