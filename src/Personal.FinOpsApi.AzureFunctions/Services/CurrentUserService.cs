using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using FunctionHttpRequestData = Microsoft.Azure.Functions.Worker.Http.HttpRequestData;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public class CurrentUserService : ICurrentUserService
{
    private static readonly string[] RoleClaimTypes =
    {
        "roles",
        "role",
        ClaimTypes.Role,
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
    };

    private readonly ILogger<CurrentUserService> _logger;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configurationManager;
    private readonly JsonWebTokenHandler _tokenHandler = new();
    private readonly List<string> _allowedAudiences = new();
    private readonly List<string> _validIssuers = new();

    public CurrentUserService(
        IConfiguration configuration,
        ILogger<CurrentUserService> logger)
    {
        _logger = logger;

        var tenantId = configuration["AZURE_TENANT_ID"] ?? configuration["EntraTenantId"];
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogWarning("[AUTH] AZURE_TENANT_ID não configurado. Tokens não poderão ser validados.");
            return;
        }

        var authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        var metadataAddress = $"{authority}/.well-known/openid-configuration";
        _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });

        _validIssuers.Add(authority);
        _validIssuers.Add($"https://sts.windows.net/{tenantId}/");

        var configuredAudiences = (configuration["OwnershipAuthAllowedAudiences"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _allowedAudiences.AddRange(configuredAudiences);

        var normalizedAudiences = _allowedAudiences
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _allowedAudiences.Clear();
        _allowedAudiences.AddRange(normalizedAudiences);
    }

    public async Task<CurrentUserContext> GetCurrentUserAsync(FunctionHttpRequestData req, CancellationToken cancellationToken = default)
    {
        var bearerToken = ExtractBearerToken(req);
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return new CurrentUserContext { IsAuthenticated = false };
        }

        if (_configurationManager == null)
        {
            _logger.LogWarning("[AUTH] Não foi possível validar token por configuração incompleta");
            return new CurrentUserContext { IsAuthenticated = false };
        }

        try
        {
            var oidc = await _configurationManager.GetConfigurationAsync(cancellationToken);
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = _validIssuers.Concat(new[] { oidc.Issuer }).Distinct(StringComparer.OrdinalIgnoreCase),
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = oidc.SigningKeys,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(5),
                ValidateAudience = _allowedAudiences.Count > 0,
                ValidAudiences = _allowedAudiences
            };

            var result = await _tokenHandler.ValidateTokenAsync(bearerToken, parameters);
            if (!result.IsValid || result.ClaimsIdentity == null)
            {
                _logger.LogWarning("[AUTH] Token inválido: {error}", result.Exception?.Message ?? "validation failed");
                return new CurrentUserContext { IsAuthenticated = false };
            }

            return BuildCurrentUser(result.ClaimsIdentity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AUTH] Erro ao validar token");
            return new CurrentUserContext { IsAuthenticated = false };
        }
    }

    private static CurrentUserContext BuildCurrentUser(ClaimsIdentity identity)
    {
        var roles = identity.Claims
            .Where(c => RoleClaimTypes.Contains(c.Type, StringComparer.OrdinalIgnoreCase))
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new CurrentUserContext
        {
            IsAuthenticated = true,
            UserId = GetClaim(identity, "oid", ClaimTypes.NameIdentifier, "sub"),
            UserPrincipalName = GetClaim(identity, "preferred_username", ClaimTypes.Upn, ClaimTypes.Email, "upn"),
            DisplayName = GetClaim(identity, "name", ClaimTypes.Name),
            Roles = roles
        };
    }

    private static string GetClaim(ClaimsIdentity identity, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = identity.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string? ExtractBearerToken(FunctionHttpRequestData req)
    {
        if (!req.Headers.TryGetValues("Authorization", out var values))
        {
            return null;
        }

        var header = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return header["Bearer ".Length..].Trim();
    }
}

public class AuthorizationService
{
    public bool IsAdmin(CurrentUserContext user)
        => user.IsAuthenticated && user.HasRole(FinOpsRoles.Admin);

    public bool CanRead(CurrentUserContext user)
        => user.IsAuthenticated && (user.HasRole(FinOpsRoles.Reader) || user.HasRole(FinOpsRoles.Admin));

    public bool CanEdit(CurrentUserContext user)
        => IsAdmin(user);
}
