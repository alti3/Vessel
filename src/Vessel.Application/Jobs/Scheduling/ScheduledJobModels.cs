using Vessel.Domain;
using Vessel.Domain.Scheduling;

namespace Vessel.Application.Jobs.Scheduling;

/// <summary>One active schedule as the dispatcher sees it.</summary>
/// <param name="Kind">The schedule kind (selects the <see cref="IScheduledJobSource" /> that runs it).</param>
/// <param name="ScheduleId">The schedule's identifier.</param>
/// <param name="TeamId">Owning team (notifications); null for instance schedules.</param>
/// <param name="Name">Display name used in missed-run notifications.</param>
/// <param name="CronExpression">Cron expression (see <see cref="Domain.ValueObjects.CronSchedule" />).</param>
/// <param name="TimeZone">Schedule time zone; null uses the instance default.</param>
/// <param name="MissedRunPolicy">Catch-up policy; null uses the instance default.</param>
/// <param name="TimeoutSeconds">Deadline of one run (interrupted-run detection).</param>
/// <param name="ActiveSince">Earliest instant occurrences are considered from when the ledger has no history.</param>
public sealed record ScheduledJobDefinition(
    ScheduledJobKind Kind,
    Guid ScheduleId,
    TeamId? TeamId,
    string Name,
    string CronExpression,
    string? TimeZone,
    MissedRunPolicy? MissedRunPolicy,
    int TimeoutSeconds,
    DateTimeOffset ActiveSince);

public enum ScheduledJobOutcomeStatus
{
    Succeeded,
    Failed,
    Skipped
}

/// <summary>Result of running one occurrence. <paramref name="Message" /> must already be redacted and short.</summary>
public sealed record ScheduledJobOutcome(ScheduledJobOutcomeStatus Status, string? Message, Guid? ExecutionId = null)
{
    public static ScheduledJobOutcome Succeeded(string? message = null, Guid? executionId = null)
    {
        return new ScheduledJobOutcome(ScheduledJobOutcomeStatus.Succeeded, message, executionId);
    }

    public static ScheduledJobOutcome Failed(string message, Guid? executionId = null)
    {
        return new ScheduledJobOutcome(ScheduledJobOutcomeStatus.Failed, message, executionId);
    }

    public static ScheduledJobOutcome Skipped(string message, Guid? executionId = null)
    {
        return new ScheduledJobOutcome(ScheduledJobOutcomeStatus.Skipped, message, executionId);
    }
}

/// <summary>
///     A kind of schedule the ledger dispatches (database backups, scheduled tasks, volume backups, control-plane
///     backups, update checks). The source lists its active schedules and runs one claimed occurrence; it records its
///     own execution history and failure tracking and must not throw for expected failures.
/// </summary>
public interface IScheduledJobSource
{
    ScheduledJobKind Kind { get; }

    IReadOnlyList<ScheduledJobDefinition> ListActiveSchedules();

    Task<ScheduledJobOutcome> RunAsync(ScheduledJobRun run, CancellationToken cancellationToken);

    /// <summary>Called when the sweeper fails a run whose worker stopped mid-run (close the source's own records).</summary>
    Task OnInterruptedAsync(ScheduledJobRun run, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

/// <summary>Instance-wide scheduling defaults (instance administrators).</summary>
/// <param name="TimeZone">IANA time zone used by schedules without their own time zone.</param>
/// <param name="MissedRunPolicy">Catch-up policy used by schedules without their own policy.</param>
public sealed record SchedulingSettings(string TimeZone = "UTC", MissedRunPolicy MissedRunPolicy = MissedRunPolicy.Skip);

public sealed record UpdateSchedulingSettingsRequest(string? TimeZone, MissedRunPolicy? MissedRunPolicy);

public sealed record ScheduledJobRunSummary(
    Guid Id,
    ScheduledJobKind Kind,
    Guid ScheduleId,
    DateTimeOffset ScheduledFor,
    ScheduledJobTrigger Trigger,
    ScheduledJobRunStatus Status,
    DateTimeOffset? EnqueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? WorkerHost,
    string? Message,
    Guid? ExecutionId);

public sealed record PagedScheduledJobRuns(IReadOnlyList<ScheduledJobRunSummary> Items, int Page, int PageSize,
    int Total);

/// <summary>Outcome of one dispatcher pass (for logs and tests).</summary>
public sealed record ScheduledJobDispatchResult(int Enqueued, int Missed, int Requeued, int Interrupted);
