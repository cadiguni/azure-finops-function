using Gvdasa.GVmodeloexemploapi.Domain.Exceptions;
using Gvdasa.GVmodeloexemploapi.Infra.Exceptions;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Filters;

public class TratamentoGlobalDeExcecao : IAsyncExceptionFilter
{
    public Task OnExceptionAsync(ExceptionContext context)
    {
        ILogger logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<TratamentoGlobalDeExcecao>>();
        logger.LogInformation(context.Exception, "Exceção interceptada ao executar {endpoint}", context.HttpContext.Request.Path);

        if (context.Exception is RecursoNaoEncontradoException)
        {
            context.Result = new NotFoundObjectResult(new ErroDto() { Erro = "Recurso não encontrado" });
        }
        else if (context.Exception is RegraDeNegocioException rnException)
        {
            context.Result = new ConflictObjectResult(new ErroDto() { Erro = rnException.Message });
        }
        else if (context.Exception is MultiTenancyException multiTenancyException)
        {
            context.Result = new UnauthorizedObjectResult(multiTenancyException.Message);
        }
        else
        {
            logger.LogError(context.Exception, "Exceção não tratada em {endpoint}", context.HttpContext.Request.Path);
            context.Result = new ObjectResult(new ErroDto(){ Erro= "Erro ao executar operação" })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }

        return Task.CompletedTask;
    }
}
