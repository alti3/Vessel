using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Email;
using Vessel.Domain.Common;
using Vessel.Shared.Errors;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     Instance email settings (upstream <c>GET/PATCH /api/v1/settings/email</c>, NTF-06). Reading requires
///     <c>settings.manage</c>; changing or test-sending additionally requires <c>secrets.write</c> (token scope
///     <c>write:sensitive</c>). The Application service also requires an instance administrator, so a team admin of
///     another team can never read or change instance mail. Secrets are write-only.
/// </summary>
[ApiController]
[Authorize(Policy = VesselPermissions.SettingsManage)]
[Route("api/v1/settings/email")]
public sealed class InstanceEmailSettingsController(InstanceEmailSettingsService settings) : ControllerBase
{
    [HttpGet]
    public ActionResult<InstanceEmailSettingsView> Get()
    {
        return Guard(() => Ok(settings.Get(User.GetUserId())));
    }

    [HttpGet("presets")]
    public ActionResult<IReadOnlyList<SmtpPresetView>> Presets()
    {
        return Guard(() => Ok(settings.ListPresets(User.GetUserId())));
    }

    [HttpPatch]
    [Authorize(Policy = VesselPermissions.SecretsWrite)]
    public async Task<ActionResult<InstanceEmailSettingsView>> Update(UpdateInstanceEmailSettingsRequest request,
        CancellationToken cancellationToken)
    {
        return await GuardAsync(async () =>
            Ok(await settings.UpdateAsync(User.GetUserId(), request, cancellationToken)));
    }

    [HttpPost("test")]
    [Authorize(Policy = VesselPermissions.SecretsWrite)]
    public async Task<ActionResult<object>> SendTest(SendTestEmailRequest request, CancellationToken cancellationToken)
    {
        return await GuardAsync(async () =>
        {
            EmailSendResult result = await settings.SendTestAsync(User.GetUserId(), request.To, cancellationToken);
            return Ok(new { success = result.Success, reason = result.FailureReason });
        });
    }

    private ActionResult Guard(Func<ActionResult> action)
    {
        try
        {
            return action();
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }
    }

    private async Task<ActionResult> GuardAsync(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (DomainException exception)
        {
            // Validation messages never echo secrets (passwords and API keys are not part of any message).
            return UnprocessableEntity(new ApiErrorResponse(new ApiError("invalid_email_settings", exception.Message,
                new Dictionary<string, object?>())));
        }
    }
}
