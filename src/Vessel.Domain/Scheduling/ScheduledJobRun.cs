using Vessel.Domain.Common;

namespace Vessel.Domain.Scheduling;

public readonly record struct ScheduledJobRunId(Guid Value) : IStronglyTypedId
{
    public static ScheduledJobRunId New()
    {
        return new ScheduledJobRunId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>The kind of schedule a run belongs to; each kind has one Application source that executes it.</summary>
public enum ScheduledJobKind
{
    DatabaseBackup = 0,
    ScheduledTask = 1,
    VolumeBackup = 2,
    ControlPlaneBackup = 3,
    UpdateCheck = 4
}

/// <summary>Lifecycle of one occurrence of a schedule (NTF-08).</summary>
public enum ScheduledJobRunStatus
{
    /// <summary>The occurrence is due and its job was enqueued; no worker has claimed it yet.</summary>
    Due = 0,

    /// <summary>A worker claimed the occurrence and is running it.</summary>
    Started = 1,

    Succeeded = 2,

    Failed = 3,

    /// <summary>The occurrence was never run (control plane down, worker lost before the claim).</summary>
    Missed = 4,

    /// <summary>The source decided not to run (resource stopped, previous run still active, schedule disabled).</summary>
    Skipped = 5
}

public enum ScheduledJobTrigger
{
    Scheduled = 0,
    Manual = 1,

    /// <summary>A missed occurrence run once after downtime (catch-up policy <see cref="MissedRunPolicy.RunOnce" />).</summary>
    CatchUp = 2
}

/// <summary>What happens to occurrences that were due while the control plane was down (older than the catch-up window).</summary>
public enum MissedRunPolicy
{
    /// <summary>Record them as missed and notify; the next regular occurrence runs as usual.</summary>
    Skip = 0,

    /// <summary>Record them as missed and run the newest one once (never once per missed occurrence).</summary>
    RunOnce = 1
}

/// <summary>
///     One occurrence of a schedule in the durable ledger (upstream <c>scheduled_job_deliveries</c>). The pair
///     (<see cref="Kind" />, <see cref="ScheduleId" />, <see cref="ScheduledFor" />) is unique for scheduled occurrences,
///     and a run is executed only by the worker that claims it, so retries, duplicate enqueues and concurrent dispatchers
///     never execute an occurrence twice.
/// </summary>
public sealed class ScheduledJobRun : Entity<ScheduledJobRunId>
{
    public const int MaxMessageLength = 2000;

    public const int MaxWorkerHostLength = 128;

    private ScheduledJobRun()
    {
    }

    private ScheduledJobRun(ScheduledJobRunId id, ScheduledJobKind kind, Guid scheduleId, TeamId? teamId,
        DateTimeOffset scheduledFor, ScheduledJobTrigger trigger, int timeoutSeconds, ScheduledJobRunStatus status,
        DateTimeOffset now)
        : base(id, now)
    {
        Kind = kind;
        ScheduleId = scheduleId;
        TeamId = teamId;
        ScheduledFor = scheduledFor.ToUniversalTime();
        Trigger = trigger;
        TimeoutSeconds = Math.Clamp(timeoutSeconds, 1, 7 * 86400);
        Status = status;
    }

    public ScheduledJobKind Kind { get; private set; }

    /// <summary>The schedule's own identifier (backup schedule, scheduled task, ...); not a foreign key.</summary>
    public Guid ScheduleId { get; private set; }

    public TeamId? TeamId { get; private set; }

    /// <summary>The occurrence time in UTC (the cron instant, or the request time for manual runs).</summary>
    public DateTimeOffset ScheduledFor { get; private set; }

    public ScheduledJobTrigger Trigger { get; private set; }

    public ScheduledJobRunStatus Status { get; private set; }

    /// <summary>Deadline of one run; used to detect runs whose worker died (interrupted claims).</summary>
    public int TimeoutSeconds { get; private set; }

    public DateTimeOffset? EnqueuedAt { get; private set; }

    /// <summary>How often the run was handed to the job queue (re-enqueued once when a queued job was lost).</summary>
    public int EnqueueCount { get; private set; }

    public Guid? ClaimToken { get; private set; }

    public string? WorkerHost { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Short, redacted outcome text (never raw command output).</summary>
    public string? Message { get; private set; }

    /// <summary>Identifier of the execution record the source created (backup execution, task execution).</summary>
    public Guid? ExecutionId { get; private set; }

    public bool IsTerminal => Status is ScheduledJobRunStatus.Succeeded or ScheduledJobRunStatus.Failed
        or ScheduledJobRunStatus.Missed or ScheduledJobRunStatus.Skipped;

    public static ScheduledJobRun CreateDue(ScheduledJobKind kind, Guid scheduleId, TeamId? teamId,
        DateTimeOffset scheduledFor, ScheduledJobTrigger trigger, int timeoutSeconds, DateTimeOffset now)
    {
        if (scheduleId == Guid.Empty) throw new DomainException("A scheduled run needs its schedule.");
        return new ScheduledJobRun(ScheduledJobRunId.New(), kind, scheduleId, teamId, scheduledFor, trigger,
            timeoutSeconds, ScheduledJobRunStatus.Due, now);
    }

    /// <summary>A missed occurrence recorded for history and alerting; it is never executed.</summary>
    public static ScheduledJobRun CreateMissed(ScheduledJobKind kind, Guid scheduleId, TeamId? teamId,
        DateTimeOffset scheduledFor, string message, DateTimeOffset now)
    {
        var run = new ScheduledJobRun(ScheduledJobRunId.New(), kind, scheduleId, teamId, scheduledFor,
            ScheduledJobTrigger.Scheduled, 1, ScheduledJobRunStatus.Missed, now);
        run.Message = Clip(message);
        run.FinishedAt = now;
        return run;
    }

    public void MarkEnqueued(DateTimeOffset now)
    {
        if (Status != ScheduledJobRunStatus.Due) throw new DomainException("Only a due run can be enqueued.");
        EnqueuedAt = now;
        EnqueueCount++;
        Touch(now);
    }

    /// <summary>Claims the run for one worker. Returns false when it is not due any more (already claimed or finished).</summary>
    public bool TryClaim(Guid claimToken, string workerHost, DateTimeOffset now)
    {
        if (Status != ScheduledJobRunStatus.Due) return false;
        Status = ScheduledJobRunStatus.Started;
        ClaimToken = claimToken;
        WorkerHost = Clip(workerHost, MaxWorkerHostLength);
        StartedAt = now;
        Touch(now);
        return true;
    }

    public void Succeed(string? message, Guid? executionId, DateTimeOffset now)
    {
        Finish(ScheduledJobRunStatus.Succeeded, message, executionId, now);
    }

    public void Fail(string? message, Guid? executionId, DateTimeOffset now)
    {
        Finish(ScheduledJobRunStatus.Failed, message, executionId, now);
    }

    public void Skip(string? message, Guid? executionId, DateTimeOffset now)
    {
        Finish(ScheduledJobRunStatus.Skipped, message, executionId, now);
    }

    /// <summary>A due run whose queued job was lost and that is too old to run now.</summary>
    public void MarkMissed(string message, DateTimeOffset now)
    {
        if (Status != ScheduledJobRunStatus.Due) throw new DomainException("Only a due run can be marked missed.");
        Status = ScheduledJobRunStatus.Missed;
        Message = Clip(message);
        FinishedAt = now;
        Touch(now);
    }

    /// <summary>True when a started run outlived its deadline plus <paramref name="margin" /> (its worker stopped).</summary>
    public bool IsInterrupted(DateTimeOffset now, TimeSpan margin)
    {
        return Status == ScheduledJobRunStatus.Started && StartedAt is { } started &&
               now - started > TimeSpan.FromSeconds(TimeoutSeconds) + margin;
    }

    private void Finish(ScheduledJobRunStatus status, string? message, Guid? executionId, DateTimeOffset now)
    {
        if (IsTerminal) return;
        Status = status;
        Message = message is null ? null : Clip(message);
        ExecutionId = executionId ?? ExecutionId;
        StartedAt ??= now;
        FinishedAt = now;
        Touch(now);
    }

    private static string Clip(string value, int max = MaxMessageLength)
    {
        return value.Length <= max ? value : value[..max];
    }
}
