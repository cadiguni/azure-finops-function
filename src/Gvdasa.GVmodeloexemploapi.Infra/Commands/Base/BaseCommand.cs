using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public abstract class BaseCommand
{
    /// <summary>
    /// Identificador do usuário que está executando a ação do command
    /// </summary>
    [JsonIgnore]
    public required int? IdUsuario { get; init; }
}
