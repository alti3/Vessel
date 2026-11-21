using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Jobs.Scheduling;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.ScheduledTasks;
using Vessel.Domain.Scheduling;
using Vessel.Domain.Servers;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.ScheduledTasks;

public static class ScheduledTaskAuditActions
{
    public const string Created = "scheduled_task.created";
    public const string Updated = "scheduled_task.updated";
    public const string Deleted = "scheduled_task.deleted";
    public const string RunQueued = "scheduled_task.run_queued";
    public const string AutoDisabled = "scheduled_task.auto_disabled";
}

/// <summary>
///     User-defined scheduled tasks (NTF-07): CRUD on an application, database or service, "run now", and execution
///     history. Permissions follow upstream Coolify: reading needs <c>applications.read</c>, changing needs
///     <c>applications.write</c>, deleting <c>applications.delete</c>, and running (or scheduling a run) needs
///     <c>deployments.start</c> (token ability <c>deploy</c>). Tasks on host-local servers (the control-plane host)
///     can be created, changed and run only by instance administrators. Execution output is shown only to callers who
///     can change the resource or read sensitive data.
/// </summary>
public sealed class ScheduledTaskService(
    IVesselDbContext dbContext,
    ScheduledTaskOwners owners,
    VesselAuthorizationService authorization,
    ScheduledJobDispatcher dispatcher,
    SchedulingSettingsService schedulingSettings,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public const int MaxTasksPerResource = 100;

    public const int MaxPageSize = 100;

    public IReadOnlyList<ScheduledTaskSummary> List(UserId actorUserId, TeamId teamId, ScheduledTaskOwnerKind kind,
        Guid ownerId)
    {
        owners.Demand(actorUserId, teamId, kind, ownerId, VesselPermissions.ApplicationsRead);
        string defaultZone = schedulingSettings.Current().TimeZone;
        return dbContext.ScheduledTasks
            .Where(task => task.TeamId == teamId && task.OwnerKind == kind && task.OwnerId == ownerId)
            .OrderBy(task => task.CreatedAt)
            .AsEnumerable()
            .Select(task => ToSummary(task, defaultZone, Now()))
            .ToArray();
    }

    public ScheduledTaskSummary Get(UserId actorUserId, TeamId teamId, ScheduledTaskId taskId)
    {
        (ScheduledTask task, _) = Load(actorUserId, teamId, taskId, VesselPermissions.ApplicationsRead);
        return ToSummary(task, schedulingSettings.Current().TimeZone, Now());
    }

    public async Task<ScheduledTaskSummary> CreateAsync(UserId actorUserId, TeamId teamId,
        ScheduledTaskOwnerKind kind, Guid ownerId, CreateScheduledTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ScheduledTaskOwner owner = owners.Demand(actorUserId, teamId, kind, ownerId,
            VesselPermissions.ApplicationsWrite);
        // Scheduling a command is scheduling a run: it needs the deploy ability as well (upstream v4.5.0).
        RequireRunPermission(actorUserId, teamId, owner);
        RequireHostLocalAdmin(actorUserId, owner);
        if (dbContext.ScheduledTasks.Count(task => task.OwnerKind == kind && task.OwnerId == ownerId) >=
            MaxTasksPerResource)
            throw new DomainException($"A resource can have at most {MaxTasksPerResource} scheduled tasks.");
        var task = ScheduledTask.Create(teamId, kind, ownerId, request.Name, request.Command, request.CronExpression,
            request.TimeZone, request.ContainerName, request.TimeoutSeconds ?? ScheduledTask.DefaultTimeoutSeconds,
            request.UseShell, request.Enabled, request.MissedRunPolicy, Now());
        await dbContext.ScheduledTaskRepository.AddAsync(task, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        // The command is not audited: it may contain credentials.
        await auditWriter.RecordAsync(teamId, actorUserId, ScheduledTaskAuditActions.Created, Target(task), null,
            Metadata(task), cancellationToken);
        return ToSummary(task, schedulingSettings.Current().TimeZone, Now());
    }

    public async Task<ScheduledTaskSummary> UpdateAsync(UserId actorUserId, TeamId teamId, ScheduledTaskId taskId,
        UpdateScheduledTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        (ScheduledTask task, ScheduledTaskOwner owner) =
            Load(actorUserId, teamId, taskId, VesselPermissions.ApplicationsWrite);
        RequireHostLocalAdmin(actorUserId, owner);
        var changesWhatRuns = request.Command is not null || request.ContainerName is not null ||
                              request.ClearContainerName || request.UseShell is not null || request.Enabled == true ||
                              request.CronExpression is not null;
        if (changesWhatRuns) RequireRunPermission(actorUserId, teamId, owner);
        task.Configure(
            request.Name ?? task.Name,
            request.Command ?? task.Command,
            request.CronExpression ?? task.CronExpression,
            request.ClearTimeZone ? null : request.TimeZone ?? task.TimeZone,
            request.ClearContainerName ? null : request.ContainerName ?? task.ContainerName,
            request.TimeoutSeconds ?? task.TimeoutSeconds,
            request.UseShell ?? task.UseShell,
            request.ClearMissedRunPolicy ? null : request.MissedRunPolicy ?? task.MissedRunPolicy,
            Now());
        if (request.Enabled == true) task.Enable(Now());
        if (request.Enabled == false) task.Disable(Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, ScheduledTaskAuditActions.Updated, Target(task), null,
            Metadata(task), cancellationToken);
        return ToSummary(task, schedulingSettings.Current().TimeZone, Now());
    }

    public async Task DeleteAsync(UserId actorUserId, TeamId teamId, ScheduledTaskId taskId,
        CancellationToken cancellationToken = default)
    {
        (ScheduledTask task, ScheduledTaskOwner owner) =
            Load(actorUserId, teamId, taskId, VesselPermissions.ApplicationsDelete);
        RequireHostLocalAdmin(actorUserId, owner);
        foreach (ScheduledTaskExecution execution in dbContext.ScheduledTaskExecutions
                     .Where(execution => execution.ScheduledTaskId == task.Id).ToArray())
            dbContext.ScheduledTaskExecutionRepository.Remove(execution);
        dbContext.ScheduledTaskRepository.Remove(task);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, ScheduledTaskAuditActions.Deleted, Target(task), null,
            Metadata(task), cancellationToken);
    }

    /// <summary>Queues a manual run through the ledger (the same claim-once path as scheduled runs).</summary>
    public async Task<ScheduledTaskRunQueued> RunNowAsync(UserId actorUserId, TeamId teamId, ScheduledTaskId taskId,
        CancellationToken cancellationToken = default)
    {
        (ScheduledTask task, ScheduledTaskOwner owner) =
            Load(actorUserId, teamId, taskId, VesselPermissions.ApplicationsRead);
        RequireRunPermission(actorUserId, teamId, owner);
        RequireHostLocalAdmin(actorUserId, owner);
        ScheduledJobRun run = await dispatcher.EnqueueManualAsync(ScheduledJobKind.ScheduledTask, task.Id.Value,
            teamId, task.TimeoutSeconds, cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, ScheduledTaskAuditActions.RunQueued, Target(task), null,
            new Dictionary<string, object?>(Metadata(task)) { ["runId"] = run.Id.Value.ToString("D") },
            cancellationToken);
        return new ScheduledTaskRunQueued(task.Id.Value, run.Id.Value);
    }

    public PagedScheduledTaskExecutions ListExecutions(UserId actorUserId, TeamId teamId, ScheduledTaskId taskId,
        int page, int pageSize)
    {
        (ScheduledTask task, ScheduledTaskOwner owner) =
            Load(actorUserId, teamId, taskId, VesselPermissions.ApplicationsRead);
        var sensitive = owners.IsAuthorized(actorUserId, teamId, owner, VesselPermissions.ApplicationsWrite) ||
                        authorization.CanReadSensitive(actorUserId, teamId, owner.AuthorizationTarget);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        IQueryable<ScheduledTaskExecution> query =
            dbContext.ScheduledTaskExecutions.Where(execution => execution.ScheduledTaskId == task.Id);
        var total = query.Count();
        ScheduledTaskExecutionSummary[] items = query.OrderByDescending(execution => execution.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsEnumerable()
            .Select(execution => ToSummary(execution, sensitive))
            .ToArray();
        return new PagedScheduledTaskExecutions(items, page, pageSize, total);
    }

    public static ScheduledTaskSummary ToSummary(ScheduledTask task, string defaultTimeZone, DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        if (task.Enabled && CronSchedule.TryParse(task.CronExpression, out CronSchedule? cron, out _) &&
            CronSchedule.TryResolveTimeZone(task.TimeZone ?? defaultTimeZone, out TimeZoneInfo? zone))
            next = cron!.GetNextOccurrence(now, zone!);
        return new ScheduledTaskSummary(task.Id.Value, task.OwnerKind, task.OwnerId, task.Name, task.Command,
            task.CronExpression, task.TimeZone, task.ContainerName, task.TimeoutSeconds, task.UseShell, task.Enabled,
            task.MissedRunPolicy, task.ConsecutiveFailures, task.FailingSince, task.DisabledReason, task.LastRunAt,
            next, task.CreatedAt, task.UpdatedAt);
    }

    public static ScheduledTaskExecutionSummary ToSummary(ScheduledTaskExecution execution, bool includeSensitive)
    {
        var hasDetails = execution.Output.Length > 0 || execution.Message is not null;
        return new ScheduledTaskExecutionSummary(execution.Id.Value, execution.ScheduledTaskId.Value,
            execution.RunId?.Value, execution.Trigger, execution.Status, execution.ContainerName, execution.StartedAt,
            execution.FinishedAt, execution.DurationSeconds, execution.ExitCode,
            includeSensitive ? execution.Output : null, execution.OutputTruncated,
            includeSensitive ? execution.Message : null, execution.WorkerHost, !includeSensitive && hasDetails);
    }

    private (ScheduledTask Task, ScheduledTaskOwner Owner) Load(UserId actorUserId, TeamId teamId,
        ScheduledTaskId taskId, string permission)
    {
        ScheduledTask? task = dbContext.ScheduledTasks.SingleOrDefault(item => item.Id == taskId);
        // Unknown tasks and other teams' tasks are reported the same way, so IDs cannot be probed.
        if (task is null || task.TeamId != teamId)
            throw new UnauthorizedAccessException("Scheduled task is outside the active team.");
        ScheduledTaskOwner owner = owners.Demand(actorUserId, teamId, task.OwnerKind, task.OwnerId, permission);
        return (task, owner);
    }

    private void RequireRunPermission(UserId actorUserId, TeamId teamId, ScheduledTaskOwner owner)
    {
        if (!owners.IsAuthorized(actorUserId, teamId, owner, VesselPermissions.DeploymentsStart))
            throw new UnauthorizedAccessException(
                $"Running scheduled tasks requires the '{VesselPermissions.DeploymentsStart}' permission.");
    }

    private void RequireHostLocalAdmin(UserId actorUserId, ScheduledTaskOwner owner)
    {
        Server server = owners.FindServer(owner) ??
                        throw new InvalidOperationException("The resource's server was not found.");
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
    }

    private static AuditTarget Target(ScheduledTask task)
    {
        return new AuditTarget("scheduled-task", task.Id.Value.ToString("D"));
    }

    private static Dictionary<string, object?> Metadata(ScheduledTask task)
    {
        return new Dictionary<string, object?>
        {
            ["ownerKind"] = task.OwnerKind.ToString(),
            ["ownerId"] = task.OwnerId.ToString("D"),
            ["name"] = task.Name,
            ["cron"] = task.CronExpression,
            ["enabled"] = task.Enabled,
            ["useShell"] = task.UseShell
        };
    }

    private DateTimeOffset Now()
    {
        return timeProvider.GetUtcNow();
    }
}
