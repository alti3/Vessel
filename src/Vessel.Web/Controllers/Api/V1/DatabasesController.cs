using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.ManagedServices;
using Vessel.Application.Resources;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ProjectsRead)]
[Route("api/v1/databases")]
public sealed class DatabasesController : ControllerBase
{
    private readonly ManagedDatabaseService _managedDatabases;
    private readonly ResourceManagementService _resources;
    private readonly DatabaseRestoreService _restores;

    public DatabasesController(ResourceManagementService resources, ManagedDatabaseService managedDatabases,
        DatabaseRestoreService restores)
    {
        _resources = resources;
        _managedDatabases = managedDatabases;
        _restores = restores;
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
        return Ok(await _resources.CreateDatabaseAsync(User.GetUserId(), User.GetTeamId(), request, cancellationToken));
    }

    [HttpPost("{databaseId:guid}/lifecycle/{action}")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult<DatabaseLifecycleResult>> Lifecycle(
        Guid databaseId,
        DatabaseLifecycleAction action,
        CancellationToken cancellationToken)
    {
        return Accepted(await _managedDatabases.QueueLifecycleActionAsync(
            User.GetUserId(),
            User.GetTeamId(),
            new DatabaseResourceId(databaseId),
            action,
            cancellationToken));
    }

    [HttpPost("{databaseId:guid}/backups")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult<BackupExecutionSummary>> BackupNow(
        Guid databaseId,
        CancellationToken cancellationToken)
    {
        return Accepted(await _managedDatabases.QueueBackupAsync(
            User.GetUserId(),
            User.GetTeamId(),
            new DatabaseResourceId(databaseId),
            cancellationToken));
    }

    [HttpPost("backup-schedules")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult<BackupScheduleSummary>> CreateSchedule(
        CreateBackupScheduleRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _managedDatabases.CreateBackupScheduleAsync(
            User.GetUserId(),
            User.GetTeamId(),
            request,
            cancellationToken));
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
        try
        {
            return Accepted(await _restores.QueueRestoreAsync(
                User.GetUserId(),
                User.GetTeamId(),
                new BackupExecutionId(backupExecutionId),
                new DatabaseResourceId(targetDatabaseId),
                new QueueDatabaseRestoreRequest(request.DryRun, request.Confirmation, request.ReplaceExisting,
                    request.KeepOwners),
                cancellationToken));
        }
        catch (DatabaseOperationConflictException exception)
        {
            return Conflict(new ProblemDetails { Title = "Database operation in progress", Detail = exception.Message });
        }
    }

    [HttpGet("restores/{restoreId:guid}")]
    public async Task<ActionResult<DatabaseRestoreSummary>> GetRestore(
        Guid restoreId,
        CancellationToken cancellationToken)
    {
        return Ok(await _restores.GetRestoreAsync(
            User.GetUserId(),
            User.GetTeamId(),
            new DatabaseRestoreId(restoreId),
            cancellationToken));
    }
}

public sealed record RestoreBackupRequest(
    bool DryRun,
    string Confirmation,
    bool ReplaceExisting = false,
    bool KeepOwners = false);
