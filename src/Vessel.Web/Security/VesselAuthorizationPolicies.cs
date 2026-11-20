using System.Security.Claims;
using Vessel.Application.Authorization;

namespace Vessel.Web.Security;

/// <summary>
///     Identity-management policies. API tokens authenticate automation; they must never manage identity (tokens,
///     memberships, team selection, second factors) unless explicitly allowed below.
/// </summary>
public static class VesselAuthorizationPolicies
{
    /// <summary>Requires an interactive cookie session. Bearer tokens (and sessions derived from them) are rejected.</summary>
    public const string InteractiveSession = "vessel.interactive-session";

    /// <summary>Requires an interactive cookie session or a bearer token that carries the <c>root</c> ability.</summary>
    public const string InteractiveSessionOrRootToken = "vessel.interactive-session-or-root-token";

    internal static bool IsInteractiveSession(ClaimsPrincipal principal)
    {
        return principal.Identity?.IsAuthenticated == true
               && !principal.IsBearerTokenPrincipal()
               && !principal.HasClaim(claim => claim.Type == VesselClaimTypes.TokenId)
               && !principal.HasClaim(claim => claim.Type == VesselClaimTypes.Permission)
               && !principal.HasClaim(claim => claim.Type == VesselClaimTypes.Scope);
    }

    internal static bool IsInteractiveSessionOrRootToken(ClaimsPrincipal principal)
    {
        return IsInteractiveSession(principal)
               || (principal.IsBearerTokenPrincipal()
                   && principal.HasClaim(VesselClaimTypes.Scope, TokenScopeMapper.Root));
    }
}
