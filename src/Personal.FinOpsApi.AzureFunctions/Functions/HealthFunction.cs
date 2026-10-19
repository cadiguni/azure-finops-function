using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

public sealed class HealthFunction
{
    [Function("health")]
    public HttpResponseData Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.WriteString(" FinOps Cost Analysis API");
        return response;
    }
}
