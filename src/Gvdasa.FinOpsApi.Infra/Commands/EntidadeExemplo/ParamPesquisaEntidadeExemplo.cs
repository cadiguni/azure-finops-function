using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class ParamPesquisaEntidadeExemplo
{
    public Guid? IdParaIgnorar { get; init; }
    public string? NomeExato { get; set; }
    public string? NomeParte { get; set; }
    public TipoExemplo? Tipo { get; set; }
    public string? Identificador { get; init; }
}
