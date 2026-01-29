using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Commands;

[ExcludeFromCodeCoverage]
public class ObterEstabelecimentosInfoCommand : IRequest<IEnumerable<EstabelecimentoInfo>>
{
    public required string IdTenant { get; init; }
    public bool IgnorarCache { get; init; } = false;
}
