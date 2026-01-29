using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.Domain.Exceptions;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Providers;

[ExcludeFromCodeCoverage]
public class TenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IJwtProvider _jwtProvider;

    public TenantProvider(IHttpContextAccessor httpContextAccessor, IJwtProvider jwtProvider)
    {
        _httpContextAccessor = httpContextAccessor;
        _jwtProvider = jwtProvider;
    }

    public string ObterTenant()
    {
        if (_httpContextAccessor.HttpContext is null)
        {
            throw new MultiTenancyException("Contexto HTTP é nulo");
        }
        else if (_httpContextAccessor.HttpContext.Request.Headers.TryGetValue("IdTenant", out var idTenant))
        {
            return idTenant.ToString();
        }
        else if (_httpContextAccessor.HttpContext.Request.Headers.TryGetValue("idTenant", out idTenant))
        {
            return idTenant.ToString();
        }

        string? tenantDoToken = _jwtProvider.ObterTenant();

        if(tenantDoToken is not null)
        {
            return tenantDoToken;
        }

        throw new MultiTenancyException("Não foi possível obter o IdTenant no header da requisição");
    }
}
