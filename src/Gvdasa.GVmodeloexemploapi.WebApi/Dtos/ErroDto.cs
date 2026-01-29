using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Dtos;

[ExcludeFromCodeCoverage]
public class ErroDto
{
    public required string Erro { get; init; }
}
