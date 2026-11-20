using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Security;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
/// Instance-administrator maintenance for the secret key ring (Coolify <c>app-key:reencrypt</c> equivalent).
/// </summary>
[ApiController]
[Authorize(Policy = VesselPermissions.SettingsManage)]
[Route("api/v1/admin/secrets")]
public sealed class SecretKeyRingController(SecretReencryptionService reencryption) : ControllerBase
{
    /// <summary>
    /// Re-encrypts stored secrets with the active key. <paramref name="dryRun"/> counts values that use an old
    /// key (and verifies they decrypt) without changing anything; otherwise a background job is queued.
    /// </summary>
    [HttpPost("reencrypt")]
    public async Task<IActionResult> Reencrypt([FromQuery] bool dryRun, CancellationToken cancellationToken)
    {
        if (dryRun)
            return Ok(await reencryption.ReencryptAsync(User.GetUserId(), dryRun: true, cancellationToken));

        string jobId = reencryption.QueueReencryption(User.GetUserId());
        return Accepted(new { jobId });
    }
}
