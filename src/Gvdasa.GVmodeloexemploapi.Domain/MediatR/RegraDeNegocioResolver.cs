using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.Domain.RegrasDeNegocio;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using Microsoft.Extensions.DependencyInjection;

namespace Gvdasa.GVmodeloexemploapi.Domain.MediatR;

[ExcludeFromCodeCoverage]
public class RegraDeNegocioResolver : IRegraDeNegocioResolver
{
    private readonly IServiceProvider _serviceProvider;

    public RegraDeNegocioResolver(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public IRegraDeNegocio<T> ObterRegra<T>() where T : EntidadeMultiTenant
    {
        return _serviceProvider.GetRequiredService<IRegraDeNegocio<T>>();
    }
}
