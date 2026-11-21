using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Jobs.Scheduling;
using Vessel.Application.ScheduledTasks;
using Vessel.Domain.ScheduledTasks;
using Vessel.Domain.Scheduling;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     User-defined scheduled tasks (NTF-07) on applications, databases and services. Reading needs
///     <c>applications.read</c>; create/update need <c>applications.write</c> (and <c>deployments.start</c>, checked by
///     the service), delete needs <c>applications.delete</c>, "run now" needs <c>deployments.start</c> (token ability
///     <c>deploy</c>). Output is returned only to callers who can change the resource or read sensitive data.
/// </summary>
[ApiController]
[Authorize(Policy = VesselPermissions.ApplicationsRead)]
[Route("api/v1")]
public sealed class ScheduledTasksController(ScheduledTaskService tasks, ScheduledJobRunQueryService runs)
    : ControllerBase
{
    [HttpGet("applications/{ownerId:guid}/scheduled-tasks")]
    public ActionResult<IReadOnlyList<ScheduledTaskSummary>> ListForApplication(Guid ownerId)
    {
        return List(ScheduledTaskOwnerKind.Application, ownerId);
    }

    [HttpGet("databases/{ownerId:guid}/scheduled-tasks")]
    public ActionResult<IReadOnlyList<ScheduledTaskSummary>> ListForDatabase(Guid ownerId)
    {
        return List(ScheduledTaskOwnerKind.Database, ownerId);
    }

    [HttpGet("services/{ownerId:guid}/scheduled-tasks")]
    public ActionResult<IReadOnlyList<ScheduledTaskSummary>> ListForService(Guid ownerId)
    {
        return List(ScheduledTaskOwnerKind.Service, ownerId);
    }

    [HttpPost("applications/{ownerId:guid}/scheduled-tasks")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public Task<ActionResult> CreateForApplication(Guid ownerId, CreateScheduledTaskRequest request,
        CancellationToken cancellationToken)
    {
        return Create(ScheduledTaskOwnerKind.Application, ownerId, request, cancellationToken);
    }

    [HttpPost("databases/{ownerId:guid}/scheduled-tasks")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public Task<ActionResult> CreateForDatabase(Guid ownerId, CreateScheduledTaskRequest request,
        CancellationToken cancellationToken)
    {
        return Create(ScheduledTaskOwnerKind.Database, ownerId, request, cancellationToken);
    }

    [HttpPost("services/{ownerId:guid}/scheduled-tasks")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public Task<ActionResult> CreateForService(Guid ownerId, CreateScheduledTaskRequest request,
        CancellationToken cancellationToken)
    {
        return Create(ScheduledTaskOwnerKind.Service, ownerId, request, cancellationToken);
    }

    [HttpGet("scheduled-tasks/{taskId:guid}")]
    public ActionResult<ScheduledTaskSummary> Get(Guid taskId)
    {
        return Guard(() => Ok(tasks.Get(User.GetUserId(), User.GetTeamId(), new ScheduledTaskId(taskId))));
    }

    [HttpPatch("scheduled-tasks/{taskId:guid}")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public Task<ActionResult> Update(Guid taskId, UpdateScheduledTaskRequest request,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await tasks.UpdateAsync(User.GetUserId(), User.GetTeamId(),
            new ScheduledTaskId(taskId), request, cancellationToken)));
    }

    [HttpDelete("scheduled-tasks/{taskId:guid}")]
    [Authorize(Policy = VesselPermissions.ApplicationsDelete)]
    public Task<ActionResult> Delete(Guid taskId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
        {
            await tasks.DeleteAsync(User.GetUserId(), User.GetTeamId(), new ScheduledTaskId(taskId),
                cancellationToken);
            return NoContent();
        });
    }

    /// <summary>Queues a manual run (202); the run goes through the same claim-once ledger as scheduled runs.</summary>
    [HttpPost("scheduled-tasks/{taskId:guid}/run")]
    [Authorize(Policy = VesselPermissions.DeploymentsStart)]
    public Task<ActionResult> Run(Guid taskId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Accepted(await tasks.RunNowAsync(User.GetUserId(), User.GetTeamId(),
            new ScheduledTaskId(taskId), cancellationToken)));
    }

    [HttpGet("scheduled-tasks/{taskId:guid}/executions")]
    public ActionResult<PagedScheduledTaskExecutions> Executions(Guid taskId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        return Guard(() => Ok(tasks.ListExecutions(User.GetUserId(), User.GetTeamId(), new ScheduledTaskId(taskId),
            page, pageSize)));
    }

    /// <summary>Ledger runs of the task (due/started/succeeded/failed/missed/skipped occurrences).</summary>
    [HttpGet("scheduled-tasks/{taskId:guid}/runs")]
    public ActionResult<PagedScheduledJobRuns> Runs(Guid taskId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        return Guard(() =>
        {
            // Authorizes the caller on the task (and hides other teams' tasks) before reading the ledger.
            tasks.Get(User.GetUserId(), User.GetTeamId(), new ScheduledTaskId(taskId));
            return Ok(runs.ListForSchedule(ScheduledJobKind.ScheduledTask, taskId, page, pageSize));
        });
    }

    private ActionResult<IReadOnlyList<ScheduledTaskSummary>> List(ScheduledTaskOwnerKind kind, Guid ownerId)
    {
        return Guard(() => Ok(tasks.List(User.GetUserId(), User.GetTeamId(), kind, ownerId)));
    }

    private Task<ActionResult> Create(ScheduledTaskOwnerKind kind, Guid ownerId, CreateScheduledTaskRequest request,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await tasks.CreateAsync(User.GetUserId(), User.GetTeamId(), kind, ownerId,
            request, cancellationToken)));
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

/// <summary>Instance scheduling defaults and the scheduled job ledger (NTF-08); instance administrators only.</summary>
[ApiController]
[Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
[Authorize(Policy = VesselPermissions.InstanceManage)]
[Route("api/v1/instance/scheduling")]
public sealed class SchedulingController(SchedulingSettingsService settings, ScheduledJobRunQueryService runs)
    : ControllerBase
{
    [HttpGet("settings")]
    public ActionResult<SchedulingSettings> GetSettings()
    {
        return Ok(settings.Get(User.GetUserId()));
    }

    [HttpPut("settings")]
    public async Task<ActionResult> UpdateSettings(UpdateSchedulingSettingsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await settings.UpdateAsync(User.GetUserId(), request, cancellationToken));
        }
        catch (Exception exception) when (ApiErrors.TryMap(this, exception, out ActionResult? result))
        {
            return result!;
        }
    }

    [HttpGet("runs")]
    public ActionResult<PagedScheduledJobRuns> Runs([FromQuery] ScheduledJobKind? kind,
        [FromQuery] ScheduledJobRunStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        return Ok(runs.ListRecent(User.GetUserId(), kind, status, page, pageSize));
    }
}
