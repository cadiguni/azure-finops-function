using System.Diagnostics.CodeAnalysis;
using Flurl;
using Flurl.Http;
using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.WebApi.Commands;
using Gvdasa.GVmodeloexemploapi.WebApi.Config;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Handlers;

[ExcludeFromCodeCoverage]
public class ObterEstabelecimentosInfoHandler
(
    IOptions<EstabelecimentosConfig> options,
    ILogger<ObterEstabelecimentosInfoHandler> logger,
    IMediator mediator,
    IMemoryCache cache
) : IRequestHandler<ObterEstabelecimentosInfoCommand, IEnumerable<EstabelecimentoInfo>>
{
    private readonly ILogger<ObterEstabelecimentosInfoHandler> _logger = logger;
    private readonly IMediator _mediator = mediator;
    private readonly IMemoryCache _cache = cache;
    private readonly string _urlEstabelecimentos = options.Value.Url;

    public async Task<IEnumerable<EstabelecimentoInfo>> Handle(ObterEstabelecimentosInfoCommand request, CancellationToken cancellationToken)
    {
        string cacheKey = CacheKey(request.IdTenant);

        IEnumerable<EstabelecimentoInfo>? resultado = _cache.Get<IEnumerable<EstabelecimentoInfo>>(cacheKey);

        if (resultado is null || request.IgnorarCache)
        {
            _logger.LogInformation("Unidades e seus estabelecimentos não existem em cache e serão buscados através de {url}", _urlEstabelecimentos);
            resultado = await BuscarUnidades(request.IdTenant, cancellationToken);
            _cache.Set(cacheKey, resultado, TimeSpan.FromMinutes(30));
        }
        else
        {
            _logger.LogInformation("Unidades e seus estabelecimentos foram obtidos do cache");
        }

        return resultado;
    }

    private static string CacheKey(string idTenant) => $"{nameof(EstabelecimentoInfo)}?idTenant={idTenant}";

    private async Task<IEnumerable<EstabelecimentoInfo>> BuscarUnidades(string idTenant, CancellationToken cancellationToken)
    {
        ObterTokenCommand tokenCommand = new()
        {
            IdTenant = idTenant
        };

        string token = await _mediator.Send(tokenCommand, cancellationToken);

        List<EstabelecimentoInfo> unidades = await _urlEstabelecimentos
            .AppendPathSegment("api/v1/Estabelecimentos")
            .AppendQueryParam("ExibirListaDesencadeada", false)
            .AppendQueryParam("OcultarContatos", true)
            .AppendQueryParam("OcultarEndereco", true)
            .WithHeader("IdTenant", idTenant)
            .WithOAuthBearerToken(token)
            .WithTimeout(TimeSpan.FromSeconds(10))
            .GetJsonAsync<List<EstabelecimentoInfo>>(cancellationToken: cancellationToken);

        return unidades;
    }
}
