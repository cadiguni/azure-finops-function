using MediatR;
using Microsoft.AspNetCore.Mvc;
using GVdasa.Cac.Attributes;
using Gvdasa.GVmodeloexemploapi.WebApi.Attributes;
using Gvdasa.GVmodeloexemploapi.WebApi.Providers;
using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;
using Gvdasa.GVmodeloexemploapi.WebApi.Extensions;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Controllers;

[AutenticarComLoginService]
[Route("api/v1/[controller]")]
[ExigirCabecalho("IdTenant")]
public class EntidadeExemploController
(
    IMediator mediator,
    ITenantProvider tenantProvider,
    IJwtProvider jwtProvider,
    ILogger<EntidadeExemploController> logger
) : Controller
{
    private readonly IMediator _mediator = mediator;
    private readonly ILogger<EntidadeExemploController> _logger = logger;

    private readonly string _idTenant = tenantProvider.ObterTenant();
    private readonly int _idUsuario = int.Parse(jwtProvider.ObterUserId()!);

    [HttpGet]
    [Route("")]
    [ProducesResponseType(typeof(EntidadeExemplo[]), StatusCodes.Status200OK)]
    public async Task<IActionResult> ObterRegistros(
        [FromQuery] ParamPesquisaEntidadeExemplo paramPesquisa,
        [FromQuery] TipoOrdenacao order = TipoOrdenacao.Asc,
        [FromQuery] string sortBy = nameof(EntidadeExemplo.Descricao),
        [FromQuery] int limite = 20,
        [FromQuery] int offset = 0)
    {
        BuscarListaEntidadeExemploCommand command = new()
        {
            IdTenant = _idTenant,
            IdUsuario = _idUsuario,
            Parametros = paramPesquisa
        };

        IEnumerable<EntidadeExemplo> registros = await _mediator.Send(command);

        Response.Headers.TryAdd("Access-Control-Expose-Headers", "X-Total-Count");
        Response.Headers.TryAdd("X-Total-Count", registros.Count().ToString());

        var retorno = registros
            .SortByProperty(sortBy, order == TipoOrdenacao.Desc)
            .Skip(offset)
            .Take(limite);

        return Ok(retorno);
    }

    [HttpGet("{id:Guid}")]
    [ProducesResponseType(typeof(EntidadeExemplo), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErroDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ObterRegistro(Guid id)
    {
        BuscarEntidadeExemploCommand command = new ()
        {
            IdTenant = _idTenant,
            IdUsuario = _idUsuario,
            Id = id
        };

        EntidadeExemplo? registro = await _mediator.Send<EntidadeExemplo?>(command);

        return registro is null
            ? NotFound(new ErroDto(){ Erro = "Registro não encontrado"})
            : Ok(registro);
    }

    [HttpPost]
    [ProducesResponseType(typeof(SingleStringDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErroDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Incluir([FromBody] IncluirEntidadeExemploDto dados)
    {
        IncluirEntidadeExemploCommand command = new()
        {
            IdTenant = _idTenant,
            IdUsuario = _idUsuario,
            Descricao = dados.Descricao,
            Nome = dados.Nome,
            Identificador = dados.Identificador,
            Tipo = dados.Tipo
        };
        EntidadeExemplo registro = await _mediator.Send(command);
        return Created(uri: null as string, value: new SingleStringDto(registro.Id.ToString()));
    }

    [HttpPut("{id:Guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErroDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Atualizar([FromRoute] Guid id, [FromBody] AtualizarEntidadeExemploDto dados)
    {
        AtualizarEntidadeExemploCommand command = new()
        {
            IdTenant = _idTenant,
            Id = id,
            IdUsuario = _idUsuario,
            Descricao = dados.Descricao,
            Nome = dados.Nome,
            Tipo = dados.Tipo,
        };

        await _mediator.Send(command);
        return NoContent();
    }

    [HttpDelete("{id:Guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErroDto), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Excluir(Guid id)
    {
        RemoverEntidadeExemploCommand command = new()
        {
            IdTenant = _idTenant,
            Id = id,
            IdUsuario = _idUsuario,
        };

        await _mediator.Send(command);
        return NoContent();
    }
}
