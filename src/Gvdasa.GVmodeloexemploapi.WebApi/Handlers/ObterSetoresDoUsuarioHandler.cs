using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.WebApi.Commands;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Handlers;

[ExcludeFromCodeCoverage]
public class ObterSetoresDoUsuarioHandler
(
    IMediator mediator
) : IRequestHandler<ObterSetoresDoUsuarioCommand, IEnumerable<SetorInfo>>
{
    private readonly IMediator _mediator = mediator;

    public async Task<IEnumerable<SetorInfo>> Handle(ObterSetoresDoUsuarioCommand request, CancellationToken cancellationToken)
    {
        ObterSetoresInfoCommand command = new()
        {
            IdTenant = request.IdTenant
        };

        IEnumerable<SetorInfo> setores = await _mediator.Send(command);

        return setores.Where(x => x.SetorUsuarios.Any(u => u.Id == request.IdUsuario));
    }

}
