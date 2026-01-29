using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Gvdasa.GVmodeloexemploapi.Infra.Extensions;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;
using MongoDB.Driver;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public abstract class BuscarListaAgrupadaEntidadeMultiTenantCommand<T>
    : MultiTenantCommand
    , IRequest<IEnumerable<T>> where T : EntidadeMultiTenant
{
    public required List<BuscarListaEntidadeMultiTenantCommand<T>> Comandos { get; set; }

    public virtual Expression<Func<T, bool>> GerarExpressionPesquisa()
    {
        return Comandos.Select(x => x.GerarExpressionPesquisa()).OrElse();
    }

    public virtual FilterDefinition<T> GerarFilter()
    {
        return Builders<T>.Filter.Or(Comandos.Select(c => c.GerarFilter()));
    }
}
