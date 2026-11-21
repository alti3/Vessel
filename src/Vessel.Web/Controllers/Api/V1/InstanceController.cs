using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Instance;
using Vessel.Domain;
using Vessel.Web.Middleware;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     Instance administration (IAM-06): settings that belong to no team (self-service registration) and the
///     instance-administrator role. Requires an interactive session of an instance administrator; team roles never
///     grant access here.
/// </summary>
[ApiController]
[Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
[Authorize(Policy = VesselPermissions.InstanceManage)]
[Route("api/v1/instance")]
public sealed class InstanceController(InstanceSettingsService instance) : ControllerBase
{
    [HttpGet("settings")]
    public ActionResult<InstanceSettingsSummary> GetSettings()
    {
        return Ok(instance.Get(User.GetUserId()));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<InstanceSettingsSummary>> UpdateSettings(
        UpdateInstanceSettingsRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await instance.UpdateAsync(User.GetUserId(), request,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext), cancellationToken));
    }

    [HttpGet("admins")]
    public ActionResult<IReadOnlyList<InstanceAdminResponse>> ListAdmins()
    {
        return Ok(instance.ListAdmins(User.GetUserId())
            .Select(admin => new InstanceAdminResponse(admin.UserId.Value, admin.Name, admin.Email))
            .ToArray());
    }

    [HttpPut("admins/{userId:guid}")]
    public async Task<IActionResult> GrantAdmin(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            await instance.GrantAdminAsync(User.GetUserId(), new UserId(userId),
                CorrelationIdMiddleware.GetCorrelationId(HttpContext), cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status404NotFound);
        }
    }

    [HttpDelete("admins/{userId:guid}")]
    public async Task<IActionResult> RevokeAdmin(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            await instance.RevokeAdminAsync(User.GetUserId(), new UserId(userId),
                CorrelationIdMiddleware.GetCorrelationId(HttpContext), cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}

public sealed record InstanceAdminResponse(Guid UserId, string Name, string Email);
