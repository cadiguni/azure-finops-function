using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Commands;

[ExcludeFromCodeCoverage]
public class ObterUsuariosInfoCommand : IRequest<IEnumerable<UsuarioInfo>>
{
    public required IEnumerable<int> Ids { get; init; }
    public required string IdTenant { get; init; }
}
