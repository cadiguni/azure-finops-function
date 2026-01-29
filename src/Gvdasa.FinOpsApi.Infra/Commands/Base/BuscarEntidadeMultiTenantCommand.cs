using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class BuscarEntidadeMultiTenantCommand<T> : MultiTenantCommand, IRequest<T?> where T : EntidadeMultiTenant
{
    public required Guid Id { get; init; }
}
