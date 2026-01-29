using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Commands;

[ExcludeFromCodeCoverage]
public class ObterSetoresInfoCommand : IRequest<IEnumerable<SetorInfo>>
{
    public required string IdTenant { get; init; }
}
