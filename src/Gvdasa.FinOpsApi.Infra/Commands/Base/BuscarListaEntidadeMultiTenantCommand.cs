using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;
using MongoDB.Driver;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public abstract class BuscarListaEntidadeMultiTenantCommand<T>
    : MultiTenantCommand
    , IRequest<IEnumerable<T>> where T : EntidadeMultiTenant
{
    /// <summary>
    /// Gerar <see cref="Expression"/> para busca no BD.
    /// Precisa ser implementado apenas quando utilizar BD SQL.
    /// </summary>
    /// <returns></returns>
    public abstract Expression<Func<T, bool>> GerarExpressionPesquisa();

    /// <summary>
    /// Gerar <see cref="FilterDefinition{T}"/> para busca no BD.
    /// Precisa ser implementado apenas quando utilizar BD Mongo.
    /// </summary>
    /// <returns></returns>
    public abstract FilterDefinition<T> GerarFilter();
}
