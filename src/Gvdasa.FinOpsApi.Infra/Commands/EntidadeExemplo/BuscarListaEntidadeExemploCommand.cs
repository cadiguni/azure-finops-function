using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class BuscarListaEntidadeExemploCommand :
    BuscarListaEntidadeMultiTenantCommand<EntidadeExemplo>, IRequest<IEnumerable<EntidadeExemplo>>
{
    public required ParamPesquisaEntidadeExemplo Parametros { init; protected get; }

    public override Expression<Func<EntidadeExemplo, bool>> GerarExpressionPesquisa()
    {
        return (x) =>
            (!Parametros.IdParaIgnorar.HasValue || !x.Id.Equals(Parametros.IdParaIgnorar.Value))
            && (string.IsNullOrWhiteSpace(Parametros.NomeParte) || x.Nome.Contains(Parametros.NomeParte))
            && (string.IsNullOrWhiteSpace(Parametros.NomeExato) || x.Nome.Equals(Parametros.NomeExato))
            && (string.IsNullOrWhiteSpace(Parametros.Identificador) || x.Identificador.Equals(Parametros.Identificador))
            && (!Parametros.Tipo.HasValue || x.Tipo == Parametros.Tipo.Value);
    }

    public override FilterDefinition<EntidadeExemplo> GerarFilter()
    {
        FilterDefinition<EntidadeExemplo> filter = Builders<EntidadeExemplo>.Filter.Where(x => x.IdTenant == IdTenant);

        if (Parametros.IdParaIgnorar.HasValue)
        {
            filter &= Builders<EntidadeExemplo>.Filter.Ne(x => x.Id, Parametros.IdParaIgnorar.Value);
        }
        if (!string.IsNullOrWhiteSpace(Parametros.NomeExato))
        {
            filter &= Builders<EntidadeExemplo>.Filter.Regex(x => x.Nome, new BsonRegularExpression($"^{Parametros.NomeExato}$", "i")); // i para case insensitive
        }
        if (!string.IsNullOrWhiteSpace(Parametros.NomeParte))
        {
            filter &= Builders<EntidadeExemplo>.Filter.Regex(x => x.Nome, new BsonRegularExpression(Parametros.NomeParte, "i")); // i para case insensitive
        }
        if (!string.IsNullOrWhiteSpace(Parametros.Identificador))
        {
            filter &= Builders<EntidadeExemplo>.Filter.Regex(x => x.Identificador, new BsonRegularExpression($"^{Parametros.Identificador}$", "i"));  // i para case insensitive
        }
        if (Parametros.Tipo.HasValue)
        {
            filter &= Builders<EntidadeExemplo>.Filter.Eq(x => x.Tipo, Parametros.Tipo);
        }

        return filter;
    }
}
