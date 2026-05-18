using Microsoft.AspNetCore.Authorization;
using Vessel.Application.Authorization;

namespace Vessel.Web.Security;

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
        if (context.User.HasClaim(VesselClaimTypes.Permission, requirement.Permission))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.Identity?.IsAuthenticated != true) return Task.CompletedTask;

        try
        {
            if (_authorizationService.HasPermission(
                    context.User.GetUserId(),
                    context.User.GetTeamId(),
                    requirement.Permission))
                context.Succeed(requirement);
        }
        catch (InvalidOperationException)
        {
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }
}
