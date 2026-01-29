using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Dtos;

[ExcludeFromCodeCoverage]
public record SetorInfo
{
    public required Guid SetorId { get; init; }
    public required string SetorNome { get; init; }
    public required IEnumerable<UsuarioInfo> SetorUsuarios { get; init; }
    public required string EstabelecimentoPaiNome { get; init; }
    public required string EstabelecimentoPaiCodigoEstruturado { get; init; }
    public required string EstabelecimentoNome { get; init; }
    public required string EstabelecimentoCodigoEstruturado { get; init; }
}
