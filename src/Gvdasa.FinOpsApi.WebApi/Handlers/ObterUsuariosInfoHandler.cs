using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Flurl;
using Flurl.Http;
using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.WebApi.Commands;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;
using GVdasa.Cac;
using GVdasa.Cac.Entidades;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Handlers;

[ExcludeFromCodeCoverage]
public class ObterUsuariosInfoHandler
(
    ICac cac,
    ILogger<ObterUsuariosInfoHandler> logger,
    IMediator mediator,
    IMemoryCache cache
) : IRequestHandler<ObterUsuariosInfoCommand, IEnumerable<UsuarioInfo>>
{
    private readonly ICac _cac = cac;
    private readonly ILogger<ObterUsuariosInfoHandler> _logger = logger;
    private readonly IMediator _mediator = mediator;
    private readonly IMemoryCache _cache = cache;

    private const string claimDeNome = "Gvdasa.ClaimTypes.NomeDoUsuario";

    private static string CacheKey(string idTenant, int id) => $"{nameof(UsuarioInfo)}?idTenant={idTenant}&id={id}";

    public async Task<IEnumerable<UsuarioInfo>> Handle(ObterUsuariosInfoCommand request, CancellationToken cancellationToken)
    {
        List<int> idsNaoEmCache = new();
        List<UsuarioInfo> usuarioEmCache = request.Ids
            .Select(idUsuario =>
            {
                UsuarioInfo? info = _cache.Get<UsuarioInfo>(CacheKey(request.IdTenant, idUsuario));

                if (info is null) idsNaoEmCache.Add(idUsuario);
                else _logger.LogInformation("Info de usuário {id} e tenant {ten} obtido do cache", idUsuario, request.IdTenant);

                return info;
            })
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

        IEnumerable<UsuarioInfo> usuariosSemCache = await BuscarUsariosInfo(request.IdTenant, idsNaoEmCache, cancellationToken);

        foreach (UsuarioInfo usuarioInfo in usuariosSemCache)
        {
            _logger.LogInformation("Info de usuário {id} e tenant {ten} obtido do LoginService", usuarioInfo.Id, request.IdTenant);
            _cache.Set(CacheKey(request.IdTenant, usuarioInfo.Id), usuarioInfo, TimeSpan.FromHours(1));
        }

        return usuarioEmCache.Concat(usuariosSemCache);
    }

    private async Task<IEnumerable<UsuarioInfo>> BuscarUsariosInfo(string idTenant, IEnumerable<int> ids, CancellationToken cancellationToken)
    {
        Tenant? tenant = await _cac.ObterTenant(idTenant);

        ObterTokenCommand tokenCommand = new()
        {
            IdTenant = idTenant
        };

        string token = await _mediator.Send(tokenCommand, cancellationToken);

        List<UsuarioInfo> usuarios = new();

        await Parallel.ForEachAsync(
            source: ids.Distinct(),
            parallelOptions: new ParallelOptions()
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4
            },
            body: async (userId, ct) =>
            {
                _logger.LogInformation("Requisição para consultar dados de usuário {id} do tenant {idTenant}. URL LS: {url}", userId, idTenant, tenant!.LoginService.Url);

                await tenant!.LoginService.Url.AppendPathSegment("/api/v1/Usuario/")
                    .AppendPathSegment(userId)
                    .WithHeader("IdTenant", idTenant)
                    .WithOAuthBearerToken(token)
                    .WithTimeout(TimeSpan.FromSeconds(10))
                    .GetJsonAsync<RespostaConsultaUsuarioDto>(cancellationToken: ct)
                    .ContinueWith(x =>
                    {
                        if (!x.IsCompletedSuccessfully)
                        {
                            _logger.LogError(x.Exception, "Não foi possível obter identificador do usuário {id} do tenant {idTenant}", userId, idTenant);
                        }
                        else
                        {
                            UsuarioDto usuario = x.Result.Resultado;
                            string? nome = usuario.Claims.FirstOrDefault(x => x.Type == claimDeNome)?.Value;

                            UsuarioInfo usuarioInfo = new(usuario.Codigo, usuario.Login, usuario.Email, nome);
                            lock (usuarios) usuarios.Add(usuarioInfo);
                        }
                    }, ct);
            });

        return usuarios;
    }
}

public record RespostaConsultaUsuarioDto(UsuarioDto Resultado);

public record UsuarioDto
{
    public required int Codigo { get; init; }
    public required string Login { get; init; }
    public required string? Email { get; init; }
    public List<ClaimDto> Claims { get; init; } = new();
}

public record ClaimDto
{
    [JsonPropertyName("m_type")]
    [JsonProperty("m_type")]
    public required string Type { get; init; }

    [JsonPropertyName("m_value")]
    [JsonProperty("m_value")]
    public required string Value { get; init; }
}
