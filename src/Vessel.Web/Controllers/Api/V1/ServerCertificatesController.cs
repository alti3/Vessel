using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Proxy;
using Vessel.Application.Proxy.Certificates;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     TLS certificate inventory of a server, mirrored from Traefik's acme.json (upstream Proxy &gt; TLS Certificates).
///     Deleting does not revoke; the proxy must be restarted for Traefik to forget a deleted certificate.
/// </summary>
[ApiController]
[Authorize(Policy = VesselPermissions.ServersRead)]
[Route("api/v1/servers/{serverId:guid}/certificates")]
public sealed class ServerCertificatesController(CertificateInventoryService inventory) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<CertificateSummary>> List(Guid serverId)
    {
        return Ok(inventory.ListForServer(User.GetUserId(), User.GetTeamId(), new ServerId(serverId)));
    }

    [HttpPost("sync")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<CertificateSyncResult>> Sync(Guid serverId, CancellationToken cancellationToken)
    {
        return Ok(await inventory.SyncServerAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId),
            cancellationToken));
    }

    [HttpDelete("{certificateId:guid}")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<CertificateDeleteResult>> Delete(
        Guid serverId,
        Guid certificateId,
        CancellationToken cancellationToken)
    {
        return Ok(await inventory.DeleteAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId),
            new CertificateId(certificateId), cancellationToken));
    }

    [HttpPost("delete-unused")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<CertificateDeleteResult>> DeleteUnused(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        return Ok(await inventory.DeleteUnusedAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId),
            cancellationToken));
    }

    [HttpGet("backups")]
    public async Task<ActionResult<IReadOnlyList<AcmeBackup>>> Backups(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        return Ok(await inventory.ListBackupsAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId),
            cancellationToken));
    }

    [HttpPost("backups/{backupName}/restore")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<CertificateDeleteResult>> RestoreBackup(
        Guid serverId,
        string backupName,
        CancellationToken cancellationToken)
    {
        return Ok(await inventory.RestoreBackupAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId),
            backupName, cancellationToken));
    }

    [HttpDelete("backups/{backupName}")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<IActionResult> DeleteBackup(Guid serverId, string backupName,
        CancellationToken cancellationToken)
    {
        await inventory.DeleteBackupAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId), backupName,
            cancellationToken);
        return NoContent();
    }
}
