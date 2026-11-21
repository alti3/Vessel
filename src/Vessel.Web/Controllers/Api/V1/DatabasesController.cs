using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Backups;
using Vessel.Application.Jobs;
using Vessel.Application.ManagedServices;
using Vessel.Application.Resources;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Shared.Errors;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     Managed databases, their lifecycle, backup schedules, backup executions (list, delete, download) and restores.
///     State-changing endpoints are POST/PATCH/DELETE only. Failure messages of executions are returned only to callers
///     with <c>secrets.read</c>, and downloading an artifact requires <c>secrets.read</c>.
/// </summary>
[ApiController]
[Authorize(Policy = VesselPermissions.ProjectsRead)]
[Route("api/v1/databases")]
public sealed class DatabasesController : ControllerBase
{
    private readonly DatabaseBackupService _backups;
    private readonly ManagedDatabaseService _managedDatabases;
    private readonly ResourceManagementService _resources;
    private readonly DatabaseRestoreService _restores;

    public DatabasesController(ResourceManagementService resources, ManagedDatabaseService managedDatabases,
        DatabaseRestoreService restores, DatabaseBackupService backups)
    {
        _resources = resources;
        _managedDatabases = managedDatabases;
        _restores = restores;
        _backups = backups;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<DatabaseSummary>> List()
    {
        return Ok(_resources.ListDatabases(User.GetUserId(), User.GetTeamId()));
    }

    [HttpPost]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult<DatabaseSummary>> Create(CreateDatabaseRequest request,
        CancellationToken cancellationToken)
    {
        return await GuardAsync(async () =>
            Ok(await _resources.CreateDatabaseAsync(User.GetUserId(), User.GetTeamId(), request, cancellationToken)));
    }

    /// <summary>Queues a lifecycle action; returns 409 while another lifecycle action, backup or restore runs.</summary>
    [HttpPost("{databaseId:guid}/lifecycle/{action}")]
    [Authorize(Policy = VesselPermissions.ResourcesLifecycle)]
    public async Task<ActionResult<DatabaseLifecycleResult>> Lifecycle(
        Guid databaseId,
        DatabaseLifecycleAction action,
        CancellationToken cancellationToken)
    {
        return await GuardAsync(async () => Accepted(await _managedDatabases.QueueLifecycleActionAsync(
            User.GetUserId(),
            User.GetTeamId(),
            new DatabaseResourceId(databaseId),
            action,
            cancellationToken)));
    }

    /// <summary>Queues a manual backup; the optional body selects one logical database.</summary>
    [HttpPost("{databaseId:guid}/backups")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult<BackupExecutionSummary>> BackupNow(
        Guid databaseId,
        [FromBody] QueueBackupRequest? request,
        CancellationToken cancellationToken)
    {
        return await GuardAsync(async () => Accepted(await _backups.QueueBackupAsync(
            User.GetUserId(),
            User.GetTeamId(),
            new DatabaseResourceId(databaseId),
            request,
            cancellationToken)));
    }

    /// <summary>Lists backup executions of a database, newest first (paged).</summary>
    [HttpGet("{databaseId:guid}/backups")]
    public ActionResult<PagedBackupExecutions> ListBackups(Guid databaseId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        return Guard(() => Ok(_backups.ListExecutions(User.GetUserId(), User.GetTeamId(),
            new DatabaseResourceId(databaseId), null, page, pageSize)));
    }

    [HttpGet("{databaseId:guid}/backup-schedules")]
    public ActionResult<IReadOnlyList<BackupScheduleSummary>> ListSchedules(Guid databaseId)
    {
        return Guard(() => Ok(_backups.ListSchedules(User.GetUserId(), User.GetTeamId(),
            new DatabaseResourceId(databaseId))));
    }

    [HttpPost("backup-schedules")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult<BackupScheduleSummary>> CreateSchedule(
        CreateBackupScheduleRequest request,
        CancellationToken cancellationToken)
    {
        return await GuardAsync(async () => Ok(await _backups.CreateScheduleAsync(
            User.GetUserId(),
            User.GetTeamId(),
            request,
            cancellationToken)));
    }

    [HttpPatch("backup-schedules/{scheduleId:guid}")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult<BackupScheduleSummary>> UpdateSchedule(
        Guid scheduleId,
        UpdateBackupScheduleRequest request,
        CancellationToken cancellationToken)
    {
        return await GuardAsync(async () => Ok(await _backups.UpdateScheduleAsync(User.GetUserId(),
            User.GetTeamId(), new BackupScheduleId(scheduleId), request, cancellationToken)));
    }

    /// <summary>Deletes a schedule and its executions; <c>deleteArtifacts=true</c> also deletes the backup files.</summary>
    [HttpDelete("backup-schedules/{scheduleId:guid}")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult> DeleteSchedule(Guid scheduleId, [FromQuery] bool deleteArtifacts,
        CancellationToken cancellationToken)
    {
        return await GuardAsync(async () =>
        {
            await _backups.DeleteScheduleAsync(User.GetUserId(), User.GetTeamId(), new BackupScheduleId(scheduleId),
                deleteArtifacts, cancellationToken);
            return NoContent();
        });
    }

    [HttpGet("backup-schedules/{scheduleId:guid}/executions")]
    public ActionResult<PagedBackupExecutions> ListScheduleExecutions(Guid scheduleId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        return Guard(() => Ok(_backups.ListScheduleExecutions(User.GetUserId(), User.GetTeamId(),
            new BackupScheduleId(scheduleId), page, pageSize)));
    }

    /// <summary>Deletes one execution and its local copy; <c>deleteRemote=true</c> also deletes remote replicas.</summary>
    [HttpDelete("backups/{backupExecutionId:guid}")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult> DeleteBackup(Guid backupExecutionId, [FromQuery] bool deleteRemote,
        CancellationToken cancellationToken)
    {
        return await GuardAsync(async () =>
        {
            await _backups.DeleteExecutionAsync(User.GetUserId(), User.GetTeamId(),
                new BackupExecutionId(backupExecutionId), deleteRemote, cancellationToken);
            return NoContent();
        });
    }

    /// <summary>Streams a backup artifact. Requires <c>secrets.read</c>: a dump contains all data of the database.</summary>
    [HttpGet("backups/{backupExecutionId:guid}/download")]
    [Authorize(Policy = VesselPermissions.SecretsRead)]
    public async Task<ActionResult> DownloadBackup(Guid backupExecutionId, CancellationToken cancellationToken)
    {
        return await GuardAsync(async () =>
        {
            BackupArtifactDownload download = await _backups.OpenDownloadAsync(User.GetUserId(), User.GetTeamId(),
                new BackupExecutionId(backupExecutionId), cancellationToken);
            HttpContext.Response.RegisterForDisposeAsync(download);
            return File(download.Content, download.ContentType, download.FileName);
        });
    }

    /// <summary>
    /// Queues a restore (or a dry-run validation) of a backup into a target database and returns 202 with the
    /// restore record to poll. Returns 409 when another restore for the target is already queued or running.
    /// </summary>
    [HttpPost("backups/{backupExecutionId:guid}/restore/{targetDatabaseId:guid}")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult<DatabaseRestoreSummary>> Restore(
        Guid backupExecutionId,
        Guid targetDatabaseId,
        RestoreBackupRequest request,
        CancellationToken cancellationToken)
    {
        return await GuardAsync(async () => Accepted(await _restores.QueueRestoreAsync(
            User.GetUserId(),
            User.GetTeamId(),
            new BackupExecutionId(backupExecutionId),
            new DatabaseResourceId(targetDatabaseId),
            new QueueDatabaseRestoreRequest(request.DryRun, request.Confirmation, request.ReplaceExisting,
                request.KeepOwners),
            cancellationToken)));
    }

    [HttpGet("restores/{restoreId:guid}")]
    public async Task<ActionResult<DatabaseRestoreSummary>> GetRestore(
        Guid restoreId,
        CancellationToken cancellationToken)
    {
        return await GuardAsync(async () => Ok(await _restores.GetRestoreAsync(
            User.GetUserId(),
            User.GetTeamId(),
            new DatabaseRestoreId(restoreId),
            cancellationToken)));
    }

    private ActionResult Guard(Func<ActionResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (Map(exception) is { } result)
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
        catch (Exception exception) when (Map(exception) is { } result)
        {
            return result;
        }
    }

    private ActionResult? Map(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => StatusCode(StatusCodes.Status403Forbidden),
            DatabaseOperationConflictException conflict => Conflict(new ProblemDetails
            {
                Title = "Database operation in progress",
                Detail = conflict.Message
            }),
            BackupDestinationUnavailableException => StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails { Title = "Backup destination unavailable" }),
            BackgroundJobsUnavailableException => StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails { Title = "Background jobs unavailable" }),
            // Domain/validation messages are written to be safe for display; they never echo secrets.
            DomainException domain => BadRequest(new ApiErrorResponse(new ApiError("invalid_database_request",
                domain.Message, new Dictionary<string, object?>()))),
            InvalidOperationException => NotFound(),
            _ => null
        };
    }
}

public sealed record RestoreBackupRequest(
    bool DryRun,
    string Confirmation,
    bool ReplaceExisting = false,
    bool KeepOwners = false);
