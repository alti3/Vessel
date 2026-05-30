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

    public DatabasesController(ResourceManagementService resources, ManagedDatabaseService managedDatabases)
    {
        _resources = resources;
        _managedDatabases = managedDatabases;
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

    [HttpPost("backups/{backupExecutionId:guid}/restore/{targetDatabaseId:guid}")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult<BackupExecutionSummary>> Restore(
        Guid backupExecutionId,
        Guid targetDatabaseId,
        RestoreBackupRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _managedDatabases.RestoreAsync(
            User.GetUserId(),
            User.GetTeamId(),
            new BackupExecutionId(backupExecutionId),
            new DatabaseResourceId(targetDatabaseId),
            request.DryRun,
            request.Confirmation,
            cancellationToken));
    }
}

public sealed record RestoreBackupRequest(bool DryRun, string Confirmation);
