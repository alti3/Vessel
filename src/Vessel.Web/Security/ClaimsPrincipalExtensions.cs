using System.Security.Claims;
using Vessel.Domain;

namespace Vessel.Web.Security;

internal static class ClaimsPrincipalExtensions
{
    public static UserId GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? throw new InvalidOperationException("Authenticated user id claim is missing.");
        return new UserId(Guid.Parse(value));
    }

    public static TeamId GetTeamId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(VesselClaimTypes.TeamId)
                    ?? throw new InvalidOperationException("Authenticated team id claim is missing.");
        return new TeamId(Guid.Parse(value));
    }

    /// <summary>The server-side session id of an interactive session, or <c>null</c>.</summary>
    public static UserSessionId? GetSessionId(this ClaimsPrincipal principal)
    {
        return Guid.TryParse(principal.FindFirstValue(VesselClaimTypes.SessionId), out Guid value)
            ? new UserSessionId(value)
            : null;
    }

    /// <summary>True when the principal was authenticated by the bearer (API token) handler.</summary>
    public static bool IsBearerTokenPrincipal(this ClaimsPrincipal principal)
    {
        return principal.Identities.Any(identity =>
            identity.IsAuthenticated
            && string.Equals(identity.AuthenticationType, VesselAuthenticationSchemes.Bearer, StringComparison.Ordinal));
    }

    /// <summary>The scopes of the presenting API token, or <c>null</c> for an interactive session.</summary>
    public static IReadOnlySet<string>? GetTokenScopes(this ClaimsPrincipal principal)
    {
        if (!principal.IsBearerTokenPrincipal()) return null;

        return principal.FindAll(VesselClaimTypes.Scope)
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
