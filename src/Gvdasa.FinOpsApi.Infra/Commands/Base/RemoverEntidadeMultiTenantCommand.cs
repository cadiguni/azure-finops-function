using System.Diagnostics.CodeAnalysis;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class RemoverEntidadeMultiTenantCommand : MultiTenantCommand, IRequest
{
    public required Guid Id { get; set; }
}
