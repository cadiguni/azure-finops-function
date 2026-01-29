using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class BuscarEntidadeCommand<T> : BaseCommand, IRequest<T?> where T : Entidade
{
    public required Guid Id { get; init; }
}
