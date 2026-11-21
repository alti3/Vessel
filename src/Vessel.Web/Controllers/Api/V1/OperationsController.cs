using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Operations.ControlPlaneBackups;
using Vessel.Application.Operations.Draining;
using Vessel.Application.Operations.Updates;
using Vessel.Application.Operations.Upgrades;
using Vessel.Domain.Operations;
using Vessel.Web.Middleware;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     Control-plane operations (OPS-01/OPS-02/DATA-13): update policy and checks, backups of Vessel's own database,
///     upgrade status and job draining. Interactive sessions of instance administrators only; the application services
///     re-check the instance-administrator role.
/// </summary>
[ApiController]
[Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
[Authorize(Policy = VesselPermissions.InstanceManage)]
[Route("api/v1/instance/operations")]
public sealed class OperationsController(
    UpdatePolicyService policies,
    UpdateCheckService updateChecks,
    ControlPlaneBackupService backups,
    UpgradeCoordinator upgrades,
    DrainService drain) : ControllerBase
{
    private string? CorrelationId => CorrelationIdMiddleware.GetCorrelationId(HttpContext);

    [HttpGet("update-policy")]
    public Task<ActionResult> GetUpdatePolicy(CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await policies.GetAsync(User.GetUserId(), cancellationToken)));
    }

    [HttpPut("update-policy")]
    public Task<ActionResult> UpdatePolicy(UpdateUpdatePolicyRequest request, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
            Ok(await policies.UpdateAsync(User.GetUserId(), request, CorrelationId, cancellationToken)));
    }

    [HttpPost("update-check")]
    public Task<ActionResult> CheckForUpdate(CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
            Ok(await updateChecks.CheckAsync(User.GetUserId(), CorrelationId, cancellationToken)));
    }

    [HttpGet("backups")]
    public Task<ActionResult> ListBackups()
    {
        return GuardAsync(() => Task.FromResult<ActionResult>(Ok(backups.List(User.GetUserId()))));
    }

    /// <summary>Queues an on-demand dump of the Vessel database (202; the dump appears in the list).</summary>
    [HttpPost("backups")]
    public Task<ActionResult> CreateBackup()
    {
        return GuardAsync(() =>
            Task.FromResult<ActionResult>(Accepted(new { jobId = backups.Queue(User.GetUserId()) })));
    }

    [HttpGet("backups/{backupId:guid}/download")]
    public Task<ActionResult> DownloadBackup(Guid backupId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
        {
            ControlPlaneBackupDownload download = await backups.OpenDownloadAsync(User.GetUserId(),
                new ControlPlaneBackupId(backupId), CorrelationId, cancellationToken);
            return File(download.Content, "application/octet-stream", download.FileName);
        });
    }

    [HttpDelete("backups/{backupId:guid}")]
    public Task<ActionResult> DeleteBackup(Guid backupId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
        {
            await backups.DeleteAsync(User.GetUserId(), new ControlPlaneBackupId(backupId), CorrelationId,
                cancellationToken);
            return NoContent();
        });
    }

    [HttpGet("backup-settings")]
    public Task<ActionResult> GetBackupSettings()
    {
        return GuardAsync(() => Task.FromResult<ActionResult>(Ok(backups.GetSettings(User.GetUserId()))));
    }

    [HttpPut("backup-settings")]
    public Task<ActionResult> UpdateBackupSettings(UpdateInstanceBackupSettingsRequest request,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
            Ok(await backups.UpdateSettingsAsync(User.GetUserId(), request, CorrelationId, cancellationToken)));
    }

    [HttpGet("upgrade")]
    public Task<ActionResult> GetUpgradeStatus(CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await upgrades.GetStatusAsync(User.GetUserId(), cancellationToken)));
    }

    /// <summary>Prepares an upgrade: pre-update backup (fail closed) and job draining. 409 while another runs.</summary>
    [HttpPost("upgrade")]
    public Task<ActionResult> BeginUpgrade(BeginUpgradeRequest request, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
            Ok(await upgrades.BeginAsync(User.GetUserId(), request, CorrelationId, cancellationToken)));
    }

    [HttpPost("upgrade/{runId:guid}/report")]
    public Task<ActionResult> ReportUpgrade(Guid runId, ReportUpgradeRequest request,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await upgrades.ReportAsync(User.GetUserId(), new UpgradeRunId(runId),
            request, CorrelationId, cancellationToken)));
    }

    [HttpPost("upgrade/{runId:guid}/abort")]
    public Task<ActionResult> AbortUpgrade(Guid runId, [FromBody] AbortUpgradeRequest? request,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await upgrades.AbortAsync(User.GetUserId(), new UpgradeRunId(runId),
            request?.Reason, CorrelationId, cancellationToken)));
    }

    [HttpGet("drain")]
    public Task<ActionResult> GetDrain(CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await drain.GetStatusAsync(User.GetUserId(), cancellationToken)));
    }

    [HttpPost("drain/start")]
    public Task<ActionResult> StartDrain([FromBody] StartDrainRequest? request, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
            Ok(await drain.StartAsync(User.GetUserId(), request?.Reason, CorrelationId, cancellationToken)));
    }

    [HttpPost("drain/release")]
    public Task<ActionResult> ReleaseDrain(CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
            Ok(await drain.ReleaseAsync(User.GetUserId(), CorrelationId, cancellationToken)));
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

public sealed record AbortUpgradeRequest(string? Reason);

public sealed record StartDrainRequest(string? Reason);
