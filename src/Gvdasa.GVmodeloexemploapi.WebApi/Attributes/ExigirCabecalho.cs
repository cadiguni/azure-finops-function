using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Attributes;

[ExcludeFromCodeCoverage]
public class ExigirCabecalho : ActionFilterAttribute
{
    private readonly string _nomeCabecalho;

    public ExigirCabecalho(string nomeCabecalho)
    {
        _nomeCabecalho = nomeCabecalho;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var valor = context.HttpContext.Request.Headers[_nomeCabecalho].ToString();
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<ExigirCabecalho>>();

        logger.LogInformation("Cabeçalho {cabecalho}: {valor}", _nomeCabecalho, valor);

        if (string.IsNullOrEmpty(valor))
        {
            logger.LogInformation("Cabeçalho {cabecalho} inválido", _nomeCabecalho);

            context.Result = new BadRequestObjectResult($"Cabeçalho {_nomeCabecalho} inválido");
        }
    }
}
