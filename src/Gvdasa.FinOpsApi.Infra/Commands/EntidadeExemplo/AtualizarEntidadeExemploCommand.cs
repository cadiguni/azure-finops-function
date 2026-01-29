using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class AtualizarEntidadeExemploCommand
    : AtualizarEntidadeMultiTenantCommand<EntidadeExemplo>, IRequest<EntidadeExemplo>, IEntidadeExemploDados
{
    Guid? IEntidadeExemploDados.Id => Id;
    public required string Nome { get; set; }
    public required string Descricao { get; set; }
    public string? Identificador { get; } = null; // identificador não é alterável via update
    public required TipoExemplo Tipo { get; init; }
}
