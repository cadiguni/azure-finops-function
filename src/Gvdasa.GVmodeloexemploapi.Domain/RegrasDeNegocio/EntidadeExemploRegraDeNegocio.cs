using Gvdasa.GVmodeloexemploapi.Domain.Exceptions;
using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Domain.RegrasDeNegocio;

public class EntidadeExemploRegraDeNegocio(IMediator mediator) : IRegraDeNegocio<EntidadeExemplo>
{
    private readonly IMediator _mediator = mediator;

    public async Task ValidarEscrita(IEscritaMultiTenantCommand<EntidadeExemplo> comando)
    {
        IEntidadeExemploDados dados = (IEntidadeExemploDados)comando;

        BuscarListaEntidadeExemploCommand mesmoNomeCmd = new()
        {
            IdTenant = comando.IdTenant,
            IdUsuario = comando.IdUsuario,
            Parametros = new ParamPesquisaEntidadeExemplo()
            {
                NomeExato = dados.Nome,
                IdParaIgnorar = dados.Id
            },
        };

        BuscarListaEntidadeExemploCommand mesmoIdentificadorCmd = new()
        {
            IdTenant = comando.IdTenant,
            IdUsuario = comando.IdUsuario,
            Parametros = new ParamPesquisaEntidadeExemplo()
            {
                Identificador = dados.Identificador
            },
        };

        BuscarListaAgrupadaEntidadeExemploCommand command = new()
        {
            IdTenant = comando.IdTenant,
            IdUsuario = comando.IdUsuario,
            Comandos = [mesmoNomeCmd, mesmoIdentificadorCmd]
        };

        IEnumerable<EntidadeExemplo> registros = await _mediator.Send(command);

        if (registros.Any(x => x.Nome.Equals(dados.Nome, StringComparison.OrdinalIgnoreCase)))
        {
            throw new NomeJaRegistradoException(dados.Nome);
        }
        if (registros.Any(x => x.Identificador.Equals(dados.Identificador, StringComparison.OrdinalIgnoreCase)))
        {
            throw new RegraDeNegocioException("Identificador já registrado");
        }
    }
}
