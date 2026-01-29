using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Dtos;

[ExcludeFromCodeCoverage]
public record AtualizarEntidadeExemploDto
{
    public required string Nome { get; set; }
    public required string Descricao { get; set; }
    public required TipoExemplo Tipo { get; set; }
}
