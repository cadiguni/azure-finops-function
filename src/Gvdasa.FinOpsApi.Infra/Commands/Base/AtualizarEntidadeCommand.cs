using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class AtualizarEntidadeCommand<T> : BaseCommand, IRequest<T> where T : Entidade
{
    [JsonIgnore]
    public required virtual Guid Id { get; init; }
}
