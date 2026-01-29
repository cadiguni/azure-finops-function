using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Flurl;
using Flurl.Http;
using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Infra.Config;
using GVdasa.Cac;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Gvdasa.GVmodeloexemploapi.Infra.Handlers;

[ExcludeFromCodeCoverage]
public sealed class ObterTokenHandler : IRequestHandler<ObterTokenCommand, string>
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly ICac _cac;
    private readonly IMemoryCache _cache;
    private readonly ILogger _logger;

    public ObterTokenHandler(
        IOptions<ClientConfig> options,
        ICac cac,
        IMemoryCache cache,
        ILogger<ObterTokenHandler> logger)
    {
        _clientId = options.Value.ClientId;
        _clientSecret = options.Value.ClientSecret;
        _cac = cac;
        _cache = cache;
        _logger = logger;
    }

    public async Task<string> Handle(ObterTokenCommand request, CancellationToken cancellationToken)
    {
        string? token = _cache.Get<string>(GerarChave(request.IdTenant));

        if (token is not null)
        {
            _logger.LogInformation("Token de client credentials do tenant {tenant} obtido do cache.", request.IdTenant);
            return token;
        }

        _logger.LogInformation("Token de client credentials do tenant {tenant} não existe em cache.", request.IdTenant);

        var data = await ObterNovoToken(request.IdTenant);
        _cache.Set(GerarChave(request.IdTenant), data.Token, TimeSpan.FromSeconds(data.TempoDeVidaEmSegundos - 30));

        return data.Token;
    }

    private string GerarChave(string idTenant) => $"TokenClientCredentials-Tenant={idTenant}";

    private async Task<TokenRequestResponse> ObterNovoToken(string idTenant)
    {
        _logger?.LogInformation("Obtendo novo token via client credentials para o tenant {tenant}. client={client}", idTenant, _clientId);

        var tenant = await _cac.ObterTenant(idTenant);

        if (tenant is null)
        {
            throw new NullReferenceException($"Tenant {idTenant} não foi localizado no CAC");
        }

        return await tenant.LoginService.Url
            .AppendPathSegment("connect/token")
            .WithTimeout(TimeSpan.FromSeconds(10))
            .PostUrlEncodedAsync(new
            {
                client_id = _clientId,
                client_secret = _clientSecret,
                grant_type = "client_credentials"
            })
            .ReceiveJson<TokenRequestResponse>();
    }
}

public record TokenRequestResponse
{
    [JsonProperty("access_token")]
    [JsonPropertyName("access_token")]
    public required string Token { get; init; }

    [JsonProperty("expires_in")]
    [JsonPropertyName("expires_in")]
    public required int TempoDeVidaEmSegundos { get; init; }
}
