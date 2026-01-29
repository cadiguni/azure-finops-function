namespace Gvdasa.GVmodeloexemploapi.WebApi.Providers;

public interface IJwtProvider
{
    string? ObterJwt();
    string? ObterUserId();
    string? ObterTenant();
}
