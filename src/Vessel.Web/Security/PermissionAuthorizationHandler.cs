using Microsoft.AspNetCore.Authorization;
using Vessel.Application.Authorization;
using Vessel.Domain;

namespace Vessel.Web.Security;

/// <summary>
///     Evaluates permission policies.
///     <list type="bullet">
///         <item>Interactive sessions: the user's current role in the active team must grant the permission.</item>
///         <item>
///             API tokens: the token's (role-clamped) permission claims must contain the permission <b>and</b> the
///             owner's current role in the token's team must grant it. A token never falls back to the owner's role.
///         </item>
///         <item>Cookie sessions carrying token claims (legacy sessions derived from a token) are rejected.</item>
///     </list>
/// </summary>
internal sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly VesselAuthorizationService _authorizationService;

    public PermissionAuthorizationHandler(VesselAuthorizationService authorizationService)
    {
        _authorizationService = authorizationService;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return Task.CompletedTask;

        if (context.User.IsBearerTokenPrincipal())
        {
            if (!context.User.HasClaim(VesselClaimTypes.Permission, requirement.Permission))
                return Task.CompletedTask;
        }
        else if (!VesselAuthorizationPolicies.IsInteractiveSession(context.User))
        {
            return Task.CompletedTask;
        }

        UserId userId;
        TeamId teamId;
        try
        {
            userId = context.User.GetUserId();
            teamId = context.User.GetTeamId();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            return Task.CompletedTask;
        }

        if (_authorizationService.HasPermission(userId, teamId, requirement.Permission))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
