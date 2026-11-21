using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Ssh.Keys;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     Team SSH keys. Responses carry the public key and fingerprint only; the private key is accepted on import and
///     never returned (it lives in the secret vault).
/// </summary>
[ApiController]
[Authorize(Policy = VesselPermissions.ServersRead)]
[Route("api/v1/security/keys")]
public sealed class SshKeysController(SshKeyService keys) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<SshKeySummary>> List()
    {
        return Guard(() => Ok(keys.List(User.GetUserId(), User.GetTeamId())));
    }

    [HttpGet("{keyId:guid}")]
    public ActionResult<SshKeySummary> Get(Guid keyId)
    {
        return Guard(() => Ok(keys.Get(User.GetUserId(), User.GetTeamId(), new SshKeyId(keyId))));
    }

    [HttpPost]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public Task<ActionResult> Create(CreateSshKeyRequest request, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
            Ok(await keys.CreateAsync(User.GetUserId(), User.GetTeamId(), request, cancellationToken)));
    }

    [HttpPost("generate")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public Task<ActionResult> Generate(GenerateSshKeyRequest request, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
            Ok(await keys.GenerateAsync(User.GetUserId(), User.GetTeamId(), request, cancellationToken)));
    }

    [HttpPatch("{keyId:guid}")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public Task<ActionResult> Update(Guid keyId, UpdateSshKeyRequest request, CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await keys.UpdateAsync(User.GetUserId(), User.GetTeamId(),
            new SshKeyId(keyId), request, cancellationToken)));
    }

    [HttpDelete("{keyId:guid}")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public Task<ActionResult> Delete(Guid keyId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
        {
            await keys.DeleteAsync(User.GetUserId(), User.GetTeamId(), new SshKeyId(keyId), cancellationToken);
            return NoContent();
        });
    }

    [HttpPost("{keyId:guid}/rotate")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public Task<ActionResult> Rotate(Guid keyId, [FromBody] RotateSshKeyRequest? request,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Accepted(await keys.RotateAsync(User.GetUserId(), User.GetTeamId(),
            new SshKeyId(keyId), request ?? new RotateSshKeyRequest(), cancellationToken)));
    }

    private ActionResult Guard(Func<ActionResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (ApiErrors.TryMap(this, exception, out ActionResult? result))
        {
            return result!;
        }
    }

    private async Task<ActionResult> GuardAsync(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (ApiErrors.TryMap(this, exception, out ActionResult? result))
        {
            return result!;
        }
    }
}
