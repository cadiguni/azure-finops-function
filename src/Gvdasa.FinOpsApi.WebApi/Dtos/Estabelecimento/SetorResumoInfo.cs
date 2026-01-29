using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Dtos;

[ExcludeFromCodeCoverage]
public record SetorResumoInfo
{
    public required Guid Id { get; init; }
    public required string Nome { get; init; }
    public IEnumerable<int> Usuarios { get; init; } = new List<int>();
}
