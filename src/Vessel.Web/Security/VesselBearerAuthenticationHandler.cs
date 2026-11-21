using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Vessel.Application.Auth;
using Vessel.Application.Authorization;

namespace Vessel.Web.Security;

internal sealed class VesselBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly VesselTokenService _tokenService;

    public VesselBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        VesselTokenService tokenService)
        : base(options, logger, encoder)
    {
        _tokenService = tokenService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? authorization = Request.Headers.Authorization;
        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var presentedToken = authorization["Bearer ".Length..].Trim();
        AuthTokenValidationResult result = await _tokenService.ValidateAsync(presentedToken, Context.RequestAborted);
        if (!result.Succeeded) return AuthenticateResult.Fail("Invalid bearer token.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.UserId.Value.ToString("D")),
            new(ClaimTypes.Name, result.Name),
            new(ClaimTypes.Email, result.Email),
            new(VesselClaimTypes.TeamId, result.TeamId.Value.ToString("D")),
            new(VesselClaimTypes.TokenId, result.TokenId.Value.ToString("D"))
        };
        claims.AddRange(result.Scopes.Select(scope => new Claim(VesselClaimTypes.Scope, scope)));
        claims.AddRange(result.Permissions.Select(permission => new Claim(VesselClaimTypes.Permission, permission)));

        // The token layer for Application authorization (abilities + optional scope limits), see IAM-05.
        Context.Items[HttpApiTokenAccessContext.ItemKey] = new ApiTokenAccess(result.TokenId, result.UserId,
            result.TeamId, result.Scopes, result.Access);

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
