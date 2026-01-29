using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Dtos;

[ExcludeFromCodeCoverage]
public record IncluirEntidadeExemploDto : AtualizarEntidadeExemploDto
{
    public required string Identificador { get; init; }
}
