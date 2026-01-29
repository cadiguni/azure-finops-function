using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.WebApi.Commands;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Handlers;

[ExcludeFromCodeCoverage]
public class ObterSetoresInfoHandler
(
    IMediator mediator
) : IRequestHandler<ObterSetoresInfoCommand, IEnumerable<SetorInfo>>
{
    private readonly IMediator _mediator = mediator;

    public async Task<IEnumerable<SetorInfo>> Handle(ObterSetoresInfoCommand request, CancellationToken cancellationToken)
    {
        IEnumerable<EstabelecimentoInfo> estabelcimentos = await ObterEstabelecimentos(request);

        IEnumerable<SetorResumoInfo> setores = estabelcimentos.SelectMany(x => x.ObterTodosSetores()).ToList();
        IEnumerable<UsuarioInfo> usuarioInfos = await ObterUsuarios(request.IdTenant, setores.SelectMany(x => x.Usuarios));

        IEnumerable<EstabelecimentoInfo> estabelecimentosDesencadeados = estabelcimentos.SelectMany(x => x.ObterEstabelecimentosDesencadeados());

        return setores.Select(setor =>
        {
            IEnumerable<UsuarioInfo> usuarios = usuarioInfos.Where(u => setor.Usuarios.Contains(u.Id));

            EstabelecimentoComSetoresInfo estabDoSetor = estabelecimentosDesencadeados
                .OfType<EstabelecimentoComSetoresInfo>()
                .First(x => x.Setores.Contains(setor));
            EstabelecimentoInfo estabPai = estabelecimentosDesencadeados.First(x => x.Estabelecimentos.Contains(estabDoSetor));

            return new SetorInfo()
            {
                SetorId = setor.Id,
                SetorNome = setor.Nome,
                SetorUsuarios = usuarios,
                EstabelecimentoCodigoEstruturado = estabDoSetor.CodigoEstruturado,
                EstabelecimentoNome = estabDoSetor.NomeExibicao,
                EstabelecimentoPaiCodigoEstruturado = estabPai.CodigoEstruturado,
                EstabelecimentoPaiNome = estabPai.NomeExibicao
            };
        });
    }

    private async Task<IEnumerable<UsuarioInfo>> ObterUsuarios(string idTenant, IEnumerable<int> idUsuarios)
    {
        ObterUsuariosInfoCommand usuariosInfoCommand = new()
        {
            Ids = idUsuarios,
            IdTenant = idTenant,
        };

        IEnumerable<UsuarioInfo> usuarioInfos = await _mediator.Send(usuariosInfoCommand);
        return usuarioInfos;
    }

    private async Task<IEnumerable<EstabelecimentoInfo>> ObterEstabelecimentos(ObterSetoresInfoCommand request)
    {
        ObterEstabelecimentosInfoCommand command = new()
        {
            IdTenant = request.IdTenant
        };

        IEnumerable<EstabelecimentoInfo> unidades = await _mediator.Send(command);
        return unidades;
    }
}
