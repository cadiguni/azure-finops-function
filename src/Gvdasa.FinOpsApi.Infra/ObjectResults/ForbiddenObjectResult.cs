using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.Infra.ObjectResults;

[ExcludeFromCodeCoverage]
public class ForbiddenObjectResult : ObjectResult
{
    public ForbiddenObjectResult(object value) : base(value)
    {
       StatusCode = StatusCodes.Status403Forbidden;
    }
}
