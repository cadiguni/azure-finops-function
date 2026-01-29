using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class IncluirEntidadeExemploCommand
    : IncluirEntidadeMultiTenantCommand<EntidadeExemplo>, IRequest<EntidadeExemplo>, IEntidadeExemploDados
{
    Guid? IEntidadeExemploDados.Id => null;
    public required string Nome { get; set; }
    public required string Descricao { get; set; }
    public required TipoExemplo Tipo { get; set; }
    public required string Identificador { get; init; }
}
