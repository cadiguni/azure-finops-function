using System.Diagnostics.CodeAnalysis;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Providers;

[ExcludeFromCodeCoverage]
public class JwtProvider : IJwtProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public JwtProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }


    public string? ObterUserId()
    {
        string? jwt = ObterJwt();

        return jwt == null
            ? null
            : new JwtSecurityToken(jwt).Payload.FirstOrDefault(p => p.Key == "uid").Value.ToString();
    }

    public string? ObterTenant()
    {
        string? jwt = ObterJwt();

        return jwt == null
            ? null
            : new JwtSecurityToken(jwt).Payload.FirstOrDefault(p => p.Key == "tenant").Value.ToString();
    }

    public string? ObterJwt()
    {
        if(_httpContextAccessor.HttpContext is null)
        {
            return null;
        }
        else if (_httpContextAccessor.HttpContext.Request.Headers.ContainsKey("Authorization"))
        {
            string jwt = _httpContextAccessor.HttpContext.Request.Headers["Authorization"].First()!;
            return jwt.Split(' ').Last(); // Bearer token_value
        }
        else if(_httpContextAccessor.HttpContext.Request.Cookies.Any(c => c.Key == "jwt"))
        {
             return _httpContextAccessor.HttpContext.Request.Cookies.First(c => c.Key == "jwt").Value;
        }

        return null;
    }

}
