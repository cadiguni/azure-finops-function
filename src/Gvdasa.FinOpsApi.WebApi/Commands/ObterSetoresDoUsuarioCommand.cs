using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Commands;

[ExcludeFromCodeCoverage]
public class ObterSetoresDoUsuarioCommand : IRequest<IEnumerable<SetorInfo>>
{
    public required string IdTenant { get; init; }
    public required int IdUsuario { get; init; }
}
