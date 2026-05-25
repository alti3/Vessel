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
}
