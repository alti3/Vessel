using System.Security.Claims;
using Vessel.Domain;

namespace Vessel.Web.Security;

/// <summary>
///     Builds the cookie-session principal from an explicit allow-list of identity claims. Token-derived claims
///     (token id, scopes, permissions) are never carried into a session, so a session cannot outlive or exceed the
///     token that created it.
/// </summary>
internal static class VesselSessionPrincipal
{
    public static ClaimsPrincipal Create(UserId userId, string name, string email, TeamId teamId)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, userId.Value.ToString("D")),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Email, email),
            new(VesselClaimTypes.TeamId, teamId.Value.ToString("D"))
        ];

        return new ClaimsPrincipal(new ClaimsIdentity(claims, VesselAuthenticationSchemes.Cookie));
    }

    /// <summary>Re-issues the current interactive session for a different active team.</summary>
    public static ClaimsPrincipal WithTeam(ClaimsPrincipal session, TeamId teamId)
    {
        return Create(
            session.GetUserId(),
            session.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            session.FindFirstValue(ClaimTypes.Email) ?? string.Empty,
            teamId);
    }
}
