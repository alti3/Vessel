using Microsoft.Extensions.Logging;
using Vessel.Application.Authorization;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Domain;
using Vessel.Domain.Notifications;
using Vessel.Domain.Scheduling;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Jobs.Scheduling;

/// <summary>
///     The single scheduler of every Vessel schedule (NTF-08, upstream <c>ScheduledJobDeliveryService</c>). It runs
///     every minute under a distributed lock, evaluates each active schedule's cron expression in its time zone,
///     records each due occurrence once in the durable ledger and enqueues a thin job that carries only the run ID.
///     <list type="bullet">
///         <item>
///             Occurrences inside the catch-up window (<see cref="CatchUpWindow" />) run; when several are pending only
///             the newest runs (a late dispatcher never bursts a schedule).
///         </item>
///         <item>
///             Older occurrences (control plane down) are recorded as one <see cref="ScheduledJobRunStatus.Missed" />
///             run and notified; with <see cref="MissedRunPolicy.RunOnce" /> the newest missed occurrence runs once.
///         </item>
///         <item>
///             Due runs whose queued job was lost (<see cref="StaleAfter" />) are re-enqueued once for backups and
///             recorded as missed otherwise; started runs that outlived their deadline plus
///             <see cref="InterruptedClaimMargin" /> are failed and never run again.
///         </item>
///     </list>
///     One failing schedule never blocks the others.
/// </summary>
public sealed partial class ScheduledJobDispatcher(
    IVesselDbContext dbContext,
    IEnumerable<IScheduledJobSource> sources,
    IBackgroundJobDispatcher jobs,
    IDistributedLockManager locks,
    SchedulingSettingsService schedulingSettings,
    VesselAuthorizationService authorization,
    INotificationEventPublisher notifications,
    TimeProvider timeProvider,
    ILogger<ScheduledJobDispatcher> logger)
{
    public const string LockKey = "scheduled-jobs:dispatch";

    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(60);

    public static readonly TimeSpan InterruptedClaimMargin = TimeSpan.FromMinutes(15);

    /// <summary>Without ledger history only the last few minutes are considered (no spurious missed runs on first start).</summary>
    public static readonly TimeSpan FirstDispatchLookback = TimeSpan.FromMinutes(2);

    /// <summary>Downtime longer than this is still reported, but only this far back is evaluated.</summary>
    public static readonly TimeSpan MaxLookback = TimeSpan.FromDays(31);

    public async Task<ScheduledJobDispatchResult> DispatchAsync(CancellationToken cancellationToken = default)
    {
        await using DistributedLockHandle? handle =
            await locks.TryAcquireAsync(LockKey, TimeSpan.FromMinutes(5), TimeSpan.Zero, cancellationToken);
        if (handle is null) return new ScheduledJobDispatchResult(0, 0, 0, 0);

        DateTimeOffset now = timeProvider.GetUtcNow();
        SchedulingSettings defaults = schedulingSettings.Current();
        var enqueued = 0;
        var missed = 0;
        foreach (IScheduledJobSource source in sources)
        {
            IReadOnlyList<ScheduledJobDefinition> definitions;
            try
            {
                definitions = source.ListActiveSchedules();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogSourceFailed(logger, source.Kind, exception);
                continue;
            }

            foreach (ScheduledJobDefinition definition in definitions)
                try
                {
                    (var queued, var lost) = await DispatchScheduleAsync(definition, defaults, now, cancellationToken);
                    enqueued += queued;
                    missed += lost;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogScheduleFailed(logger, definition.Kind, definition.ScheduleId, exception);
                }
        }

        (var requeued, var staleMissed, var interrupted) = await SweepAsync(now, cancellationToken);
        return new ScheduledJobDispatchResult(enqueued + requeued, missed + staleMissed, requeued, interrupted);
    }

    /// <summary>Records a manual ("run now") occurrence and enqueues it. The caller has authorized the actor.</summary>
    public async Task<ScheduledJobRun> EnqueueManualAsync(ScheduledJobKind kind, Guid scheduleId, TeamId? teamId,
        int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var run = ScheduledJobRun.CreateDue(kind, scheduleId, teamId, now, ScheduledJobTrigger.Manual,
            timeoutSeconds, now);
        await dbContext.ScheduledJobRunRepository.AddAsync(run, cancellationToken);
        run.MarkEnqueued(now);
        await dbContext.SaveChangesAsync(cancellationToken);
        Enqueue(run);
        return run;
    }

    private async Task<(int Enqueued, int Missed)> DispatchScheduleAsync(ScheduledJobDefinition definition,
        SchedulingSettings defaults, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!CronSchedule.TryParse(definition.CronExpression, out CronSchedule? cron, out _)) return (0, 0);
        if (!CronSchedule.TryResolveTimeZone(definition.TimeZone ?? defaults.TimeZone, out TimeZoneInfo? zone))
            zone = TimeZoneInfo.Utc;

        DateTimeOffset? last = dbContext.ScheduledJobRuns
            .Where(run => run.Kind == definition.Kind && run.ScheduleId == definition.ScheduleId &&
                          run.Trigger != ScheduledJobTrigger.Manual)
            .Select(run => (DateTimeOffset?)run.ScheduledFor)
            .Max();
        DateTimeOffset from = last ?? Max(definition.ActiveSince, now - FirstDispatchLookback);
        if (from < now - MaxLookback) from = now - MaxLookback;
        IReadOnlyList<DateTimeOffset> occurrences = cron!.GetOccurrences(from, now, zone!, 10_000);
        if (occurrences.Count == 0) return (0, 0);

        DateTimeOffset windowStart = now - CatchUpWindow;
        DateTimeOffset[] recent = occurrences.Where(occurrence => occurrence > windowStart).ToArray();
        DateTimeOffset[] older = occurrences.Where(occurrence => occurrence <= windowStart).ToArray();
        MissedRunPolicy policy = definition.MissedRunPolicy ?? defaults.MissedRunPolicy;
        var toRun = new List<(DateTimeOffset At, ScheduledJobTrigger Trigger)>();
        if (recent.Length > 0) toRun.Add((recent[^1], ScheduledJobTrigger.Scheduled));

        DateTimeOffset[] missedOccurrences = older;
        if (recent.Length == 0 && older.Length > 0 && policy == MissedRunPolicy.RunOnce)
        {
            toRun.Add((older[^1], ScheduledJobTrigger.CatchUp));
            missedOccurrences = older[..^1];
        }

        var runs = new List<ScheduledJobRun>();
        if (missedOccurrences.Length > 0)
        {
            DateTimeOffset newestMissed = missedOccurrences[^1];
            var message = missedOccurrences.Length == 1
                ? $"The occurrence at {newestMissed.UtcDateTime:yyyy-MM-dd HH:mm} UTC was not run (the control plane was not running)."
                : $"{missedOccurrences.Length} occurrences between {missedOccurrences[0].UtcDateTime:yyyy-MM-dd HH:mm} and " +
                  $"{newestMissed.UtcDateTime:yyyy-MM-dd HH:mm} UTC were not run (the control plane was not running).";
            runs.Add(ScheduledJobRun.CreateMissed(definition.Kind, definition.ScheduleId, definition.TeamId,
                newestMissed, message, now));
        }

        foreach ((DateTimeOffset at, ScheduledJobTrigger trigger) in toRun)
        {
            // Unique occurrence per schedule: a concurrent or repeated dispatcher pass never records it twice.
            var exists = dbContext.ScheduledJobRuns.Any(run => run.Kind == definition.Kind &&
                                                               run.ScheduleId == definition.ScheduleId &&
                                                               run.ScheduledFor == at &&
                                                               run.Trigger != ScheduledJobTrigger.Manual);
            if (exists) continue;
            ScheduledJobRun run = ScheduledJobRun.CreateDue(definition.Kind, definition.ScheduleId,
                definition.TeamId, at, trigger, definition.TimeoutSeconds, now);
            run.MarkEnqueued(now);
            runs.Add(run);
        }

        if (runs.Count == 0) return (0, 0);
        foreach (ScheduledJobRun run in runs) await dbContext.ScheduledJobRunRepository.AddAsync(run, cancellationToken);
        // Save first: a job is enqueued only for a run that is durably recorded.
        await dbContext.SaveChangesAsync(cancellationToken);
        var queued = 0;
        foreach (ScheduledJobRun run in runs.Where(run => run.Status == ScheduledJobRunStatus.Due))
        {
            Enqueue(run);
            queued++;
        }

        if (missedOccurrences.Length > 0)
            await NotifyMissedAsync(definition, missedOccurrences.Length, missedOccurrences[^1], cancellationToken);
        return (queued, missedOccurrences.Length > 0 ? 1 : 0);
    }

    private async Task<(int Requeued, int Missed, int Interrupted)> SweepAsync(DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DateTimeOffset staleBefore = now - StaleAfter;
        ScheduledJobRun[] stale = dbContext.ScheduledJobRuns
            .Where(run => run.Status == ScheduledJobRunStatus.Due && run.EnqueuedAt != null &&
                          run.EnqueuedAt < staleBefore)
            .Take(500)
            .ToArray();
        ScheduledJobRun[] started = dbContext.ScheduledJobRuns
            .Where(run => run.Status == ScheduledJobRunStatus.Started)
            .Take(500)
            .AsEnumerable()
            .Where(run => run.IsInterrupted(now, InterruptedClaimMargin))
            .ToArray();
        if (stale.Length == 0 && started.Length == 0) return (0, 0, 0);

        var requeue = new List<ScheduledJobRun>();
        var missed = new List<ScheduledJobRun>();
        foreach (ScheduledJobRun run in stale)
            if (run.EnqueueCount < 2 && IsRequeueable(run.Kind))
            {
                run.MarkEnqueued(now);
                requeue.Add(run);
            }
            else
            {
                run.MarkMissed("The queued job was lost before a worker started it; it was not run.", now);
                missed.Add(run);
            }

        foreach (ScheduledJobRun run in started)
        {
            run.Fail("The worker stopped while this run was running. It is not run again.", null, now);
            IScheduledJobSource? source = sources.FirstOrDefault(item => item.Kind == run.Kind);
            if (source is not null)
                try
                {
                    await source.OnInterruptedAsync(run, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogScheduleFailed(logger, run.Kind, run.ScheduleId, exception);
                }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        foreach (ScheduledJobRun run in requeue) Enqueue(run);
        return (requeue.Count, missed.Count, started.Length);
    }

    private void Enqueue(ScheduledJobRun run)
    {
        jobs.Enqueue<RunScheduledJobOccurrenceJob>(job => job.RunAsync(run.Id.Value, CancellationToken.None));
    }

    private async Task NotifyMissedAsync(ScheduledJobDefinition definition, int count, DateTimeOffset newest,
        CancellationToken cancellationToken)
    {
        RaiseNotificationRequest notification = ScheduledJobNotifications.Missed(definition.Kind,
            definition.ScheduleId, definition.Name, count, newest);
        if (definition.TeamId is { } teamId)
        {
            await notifications.PublishAsync(teamId, notification, cancellationToken);
            return;
        }

        foreach (TeamId team in dbContext.Teams.Select(team => team.Id).ToArray()
                     .Where(authorization.TeamHasInstanceAdmin))
            await notifications.PublishAsync(team, notification, cancellationToken);
    }

    private static bool IsRequeueable(ScheduledJobKind kind)
    {
        // Upstream requeues lost backups; a lost command (scheduled task) is never run late.
        return kind is ScheduledJobKind.DatabaseBackup or ScheduledJobKind.VolumeBackup
            or ScheduledJobKind.ControlPlaneBackup or ScheduledJobKind.UpdateCheck;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right)
    {
        return left > right ? left : right;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Listing {Kind} schedules failed; they are retried next minute.")]
    private static partial void LogSourceFailed(ILogger logger, ScheduledJobKind kind, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dispatching {Kind} schedule {ScheduleId} failed.")]
    private static partial void LogScheduleFailed(ILogger logger, ScheduledJobKind kind, Guid scheduleId,
        Exception exception);
}

/// <summary>Notifications of the scheduled job ledger. Messages carry names and times only, never command output.</summary>
public static class ScheduledJobNotifications
{
    public static RaiseNotificationRequest Missed(ScheduledJobKind kind, Guid scheduleId, string name, int count,
        DateTimeOffset newest)
    {
        var what = kind switch
        {
            ScheduledJobKind.DatabaseBackup => "Backup schedule",
            ScheduledJobKind.VolumeBackup => "Volume backup schedule",
            ScheduledJobKind.ScheduledTask => "Scheduled task",
            ScheduledJobKind.ControlPlaneBackup => "Control-plane backup",
            _ => "Schedule"
        };
        var occurrences = count == 1 ? "1 occurrence was" : $"{count} occurrences were";
        return new RaiseNotificationRequest(NotificationEventTypes.ScheduledJobMissed, NotificationSeverity.Warning,
            kind == ScheduledJobKind.ScheduledTask ? NotificationTargetType.ScheduledTask : NotificationTargetType.System,
            scheduleId.ToString("D"), $"{what} missed: {name}",
            $"{what} '{name}': {occurrences} not run while Vessel was not running (latest {newest.UtcDateTime:yyyy-MM-dd HH:mm} UTC).",
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["kind"] = kind.ToString(),
                ["scheduleId"] = scheduleId,
                ["missed"] = count
            }), null, null);
    }
}
