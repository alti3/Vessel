using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Backups;
using Vessel.Application.Backups.Volumes;
using Vessel.Application.Jobs;
using Vessel.Application.Storage.Persistent;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Storage;
using Vessel.Shared.Errors;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     Persistent storage of applications and services (DATA-12) and the backup schedule of each volume or directory
///     (DATA-09): <c>/api/v1/{applications|services}/{id}/storages[/{storageId}[/backups]]</c>. Requests carrying a
///     <c>host_path</c> (or <c>hostPath</c>) are rejected: arbitrary host paths do not exist (upstream v4.3.22).
/// </summary>
[ApiController]
[Authorize(Policy = VesselPermissions.ProjectsRead)]
[Route("api/v1/{ownerKind:regex(^(applications|services)$)}/{ownerId:guid}/storages")]
public sealed class ResourceStoragesController(ResourceStorageService storages, VolumeBackupService backups)
    : ControllerBase
{
    private static readonly string[] HostPathFields = ["host_path", "hostPath", "HostPath", "hostpath"];

    [HttpGet]
    public ActionResult<IReadOnlyList<ResourceStorageSummary>> List(string ownerKind, Guid ownerId)
    {
        return Guard(() => Ok(storages.List(User.GetUserId(), User.GetTeamId(), Kind(ownerKind), ownerId)));
    }

    /// <summary>One entry; <c>includeContent=true</c> returns file content and requires <c>secrets.read</c>.</summary>
    [HttpGet("{storageId:guid}")]
    public Task<ActionResult> Get(string ownerKind, Guid ownerId, Guid storageId, [FromQuery] bool includeContent,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await storages.GetAsync(User.GetUserId(), User.GetTeamId(), Kind(ownerKind),
            ownerId, storageId, includeContent, cancellationToken)));
    }

    [HttpPost]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public Task<ActionResult> Create(string ownerKind, Guid ownerId, CreateStorageBody body,
        CancellationToken cancellationToken)
    {
        if (RejectHostPath(body.Extra) is { } rejected) return Task.FromResult(rejected);
        return GuardAsync(async () => Ok(await storages.CreateAsync(User.GetUserId(), User.GetTeamId(),
            Kind(ownerKind), ownerId,
            new CreateResourceStorageRequest(body.Type, body.MountPath, body.Name, body.Path, body.Content,
                body.IsHostFile, body.ServiceName), cancellationToken)));
    }

    [HttpPatch("{storageId:guid}")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public Task<ActionResult> Update(string ownerKind, Guid ownerId, Guid storageId, UpdateStorageBody body,
        CancellationToken cancellationToken)
    {
        if (RejectHostPath(body.Extra) is { } rejected) return Task.FromResult(rejected);
        return GuardAsync(async () => Ok(await storages.UpdateAsync(User.GetUserId(), User.GetTeamId(),
            Kind(ownerKind), ownerId, storageId,
            new UpdateResourceStorageRequest(body.MountPath, body.Name, body.Path, body.Content, body.ClearContent,
                body.ServiceName, body.ConfirmRename), cancellationToken)));
    }

    [HttpDelete("{storageId:guid}")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public Task<ActionResult> Delete(string ownerKind, Guid ownerId, Guid storageId,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
        {
            await storages.DeleteAsync(User.GetUserId(), User.GetTeamId(), Kind(ownerKind), ownerId, storageId,
                cancellationToken);
            return NoContent();
        });
    }

    /// <summary>The backup schedule of this storage, or 404 when it has none.</summary>
    [HttpGet("{storageId:guid}/backups")]
    public ActionResult<VolumeBackupScheduleSummary> GetBackupSchedule(string ownerKind, Guid ownerId,
        Guid storageId)
    {
        return Guard(() =>
        {
            VolumeBackupScheduleSummary? schedule = backups
                .ListSchedules(User.GetUserId(), User.GetTeamId(), Kind(ownerKind), ownerId)
                .SingleOrDefault(item => item.TargetId == storageId);
            return schedule is null ? NotFound() : Ok(schedule);
        });
    }

    [HttpPost("{storageId:guid}/backups")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public Task<ActionResult> CreateBackupSchedule(string ownerKind, Guid ownerId, Guid storageId,
        CreateVolumeBackupScheduleRequest request, CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await backups.CreateScheduleAsync(User.GetUserId(), User.GetTeamId(),
            Kind(ownerKind), ownerId, storageId, request, cancellationToken)));
    }

    private static StorageOwnerKind Kind(string ownerKind)
    {
        return ownerKind == "services" ? StorageOwnerKind.Service : StorageOwnerKind.Application;
    }

    private ActionResult? RejectHostPath(IDictionary<string, JsonElement>? extra)
    {
        if (extra is null || !extra.Keys.Any(key => HostPathFields.Contains(key, StringComparer.OrdinalIgnoreCase)))
            return null;
        return BadRequest(new ApiErrorResponse(new ApiError("host_path_not_supported",
            "Host paths are not supported. Use a named volume, or a file or directory path relative to the resource directory.",
            new Dictionary<string, object?>())));
    }

    private ActionResult Guard(Func<ActionResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (StorageApiErrors.Map(this, exception) is { } result)
        {
            return result;
        }
    }

    private async Task<ActionResult> GuardAsync(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (StorageApiErrors.Map(this, exception) is { } result)
        {
            return result;
        }
    }
}

public sealed record CreateStorageBody(
    ResourceStorageType Type,
    string MountPath,
    string? Name = null,
    string? Path = null,
    string? Content = null,
    bool IsHostFile = false,
    string? ServiceName = null)
{
    [JsonExtensionData] public IDictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record UpdateStorageBody(
    string? MountPath = null,
    string? Name = null,
    string? Path = null,
    string? Content = null,
    bool ClearContent = false,
    string? ServiceName = null,
    bool ConfirmRename = false)
{
    [JsonExtensionData] public IDictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
///     Volume backup schedules, executions and restores (DATA-09). Failure and recovery messages are returned only to
///     callers with <c>secrets.read</c>.
/// </summary>
[ApiController]
[Authorize(Policy = VesselPermissions.ProjectsRead)]
[Route("api/v1/volume-backups")]
public sealed class VolumeBackupsController(VolumeBackupService backups) : ControllerBase
{
    [HttpPatch("{scheduleId:guid}")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public Task<ActionResult> Update(Guid scheduleId, UpdateVolumeBackupScheduleRequest request,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await backups.UpdateScheduleAsync(User.GetUserId(), User.GetTeamId(),
            new ScheduledVolumeBackupId(scheduleId), request, cancellationToken)));
    }

    /// <summary>Deletes the schedule; archives are deleted too unless <c>deleteArtifacts=false</c>.</summary>
    [HttpDelete("{scheduleId:guid}")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public Task<ActionResult> Delete(Guid scheduleId, CancellationToken cancellationToken,
        [FromQuery] bool deleteArtifacts = true)
    {
        return GuardAsync(async () =>
        {
            await backups.DeleteScheduleAsync(User.GetUserId(), User.GetTeamId(),
                new ScheduledVolumeBackupId(scheduleId), deleteArtifacts, cancellationToken);
            return NoContent();
        });
    }

    [HttpPost("{scheduleId:guid}/run")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public Task<ActionResult> Run(Guid scheduleId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Accepted(await backups.RunNowAsync(User.GetUserId(), User.GetTeamId(),
            new ScheduledVolumeBackupId(scheduleId), cancellationToken)));
    }

    [HttpGet("{scheduleId:guid}/executions")]
    public Task<ActionResult> Executions(Guid scheduleId, CancellationToken cancellationToken,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        return GuardAsync(async () => Ok(await backups.ListExecutionsAsync(User.GetUserId(), User.GetTeamId(),
            new ScheduledVolumeBackupId(scheduleId), page, pageSize, cancellationToken)));
    }

    [HttpDelete("executions/{executionId:guid}")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public Task<ActionResult> DeleteExecution(Guid executionId, [FromQuery] bool deleteRemote,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
        {
            await backups.DeleteExecutionAsync(User.GetUserId(), User.GetTeamId(),
                new VolumeBackupExecutionId(executionId), deleteRemote, cancellationToken);
            return NoContent();
        });
    }

    [HttpPost("executions/{executionId:guid}/restore")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public Task<ActionResult> Restore(Guid executionId, QueueVolumeRestoreRequest request,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Accepted(await backups.QueueRestoreAsync(User.GetUserId(), User.GetTeamId(),
            new VolumeBackupExecutionId(executionId), request, cancellationToken)));
    }

    /// <summary>Retries restarting containers that a backup left stopped ("needs attention").</summary>
    [HttpPost("executions/{executionId:guid}/recover")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public Task<ActionResult> Recover(Guid executionId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Accepted(await backups.RetryRecoveryAsync(User.GetUserId(), User.GetTeamId(),
            new VolumeBackupExecutionId(executionId), cancellationToken)));
    }

    [HttpGet("restores/{restoreId:guid}")]
    public Task<ActionResult> GetRestore(Guid restoreId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await backups.GetRestoreAsync(User.GetUserId(), User.GetTeamId(),
            new VolumeBackupRestoreId(restoreId), cancellationToken)));
    }

    private async Task<ActionResult> GuardAsync(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (StorageApiErrors.Map(this, exception) is { } result)
        {
            return result;
        }
    }
}

/// <summary>Maps storage, volume backup and import exceptions; their messages are written for display.</summary>
internal static class StorageApiErrors
{
    public static ActionResult? Map(ControllerBase controller, Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => controller.StatusCode(StatusCodes.Status403Forbidden),
            VolumeBackupConflictException or Vessel.Application.ManagedServices.DatabaseOperationConflictException =>
                controller.Conflict(new ProblemDetails { Title = "Operation in progress", Detail = exception.Message }),
            BackupDestinationUnavailableException => controller.StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails { Title = "Backup destination unavailable" }),
            BackgroundJobsUnavailableException => controller.StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails { Title = "Background jobs unavailable" }),
            DomainException domain => controller.BadRequest(new ApiErrorResponse(new ApiError("invalid_request",
                domain.Message, new Dictionary<string, object?>()))),
            InvalidOperationException => controller.NotFound(),
            _ => null
        };
    }
}
