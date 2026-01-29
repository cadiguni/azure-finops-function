using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Gvdasa.GVmodeloexemploapi.Domain.Exceptions;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;
using System.Threading.Tasks;
using Gvdasa.GVmodeloexemploapi.Infra.Exceptions;
using System.Collections.Generic;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Filters.Tests;

[TestClass]
public class TratamentoGlobalDeExcecaoTests
{
    private readonly TratamentoGlobalDeExcecao _filter;
    private readonly ActionContext _actionContext;
    private readonly Mock<ILogger<TratamentoGlobalDeExcecao>> _loggerMock;

    public TratamentoGlobalDeExcecaoTests()
    {
        _filter = new TratamentoGlobalDeExcecao();

        var httpContext = new DefaultHttpContext();
        _loggerMock = new Mock<ILogger<TratamentoGlobalDeExcecao>>();
        httpContext.RequestServices = new ServiceCollection()
            .AddSingleton(_loggerMock.Object)
            .BuildServiceProvider();

        var actionDescriptor = new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor(); // Use a valid ActionDescriptor
        var routeData = new Microsoft.AspNetCore.Routing.RouteData(); // Use a valid RouteData

        _actionContext = new ActionContext(httpContext, routeData, actionDescriptor);
    }

    [TestMethod]
    public async Task OnExceptionAsync_RecursoNaoEncontradoException_DeveRetornarNotFoundObjectResult()
    {
        // Arrange
        var context = new ExceptionContext(_actionContext, new List<IFilterMetadata>())
        {
            Exception = new RecursoNaoEncontradoException("teste")
        };

        // Act
        await _filter.OnExceptionAsync(context);

        // Assert
        Assert.IsInstanceOfType(context.Result, typeof(NotFoundObjectResult));
        var result = (NotFoundObjectResult)context.Result;
        Assert.IsInstanceOfType(result.Value, typeof(ErroDto));
        var erroDto = (ErroDto)result.Value;
        Assert.AreEqual("Recurso não encontrado", erroDto.Erro);
    }

    [TestMethod]
    public async Task OnExceptionAsync_RegraDeNegocioException_DeveRetornarConflictObjectResult()
    {
        // Arrange
        var context = new ExceptionContext(_actionContext, new List<IFilterMetadata>())
        {
            Exception = new RegraDeNegocioException("Teste")
        };

        // Act
        await _filter.OnExceptionAsync(context);

        // Assert
        Assert.IsInstanceOfType(context.Result, typeof(ConflictObjectResult));
        var result = (ConflictObjectResult)context.Result;
        Assert.IsInstanceOfType(result.Value, typeof(ErroDto));
        var erroDto = (ErroDto)result.Value;
        Assert.IsTrue(erroDto.Erro.Length > 0);
    }

    [TestMethod]
    public async Task OnExceptionAsync_ExceptionNaoTratada_DeveRetornarInternalServerError()
    {
        // Arrange
        var context = new ExceptionContext(_actionContext, new List<IFilterMetadata>())
        {
            Exception = new System.Exception("Teste")
        };

        // Act
        await _filter.OnExceptionAsync(context);

        // Assert
        ObjectResult result = (ObjectResult)context.Result!;
        Assert.AreEqual(500, result.StatusCode);
        Assert.IsInstanceOfType(result.Value, typeof(ErroDto));
        var erroDto = (ErroDto)result.Value;
        Assert.IsTrue(erroDto.Erro.Length > 0);
    }

    [TestMethod]
    public async Task OnExceptionAsync_MultiTenancyException_DeveRetornarUnauthorizedObjectResult()
    {
        // Arrange
        var context = new ExceptionContext(_actionContext, new List<IFilterMetadata>())
        {
            Exception = new MultiTenancyException("Teste")
        };

        // Act
        await _filter.OnExceptionAsync(context);

        // Assert
        Assert.IsInstanceOfType(context.Result, typeof(UnauthorizedObjectResult));
    }

}
