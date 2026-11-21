using Microsoft.Extensions.Logging;
using Vessel.Application.Operations.Draining;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain.Scheduling;

namespace Vessel.Application.Jobs.Scheduling;

/// <summary>
///     Executes one ledger run: claims it (only a <see cref="ScheduledJobRunStatus.Due" /> run can be claimed, so a
///     duplicate or retried job does nothing), records the worker host, runs the source and records the outcome.
///     Exceptions become a failed run; nothing is rethrown, so Hangfire never retries an occurrence.
/// </summary>
public sealed partial class ScheduledJobRunner(
    IVesselDbContext dbContext,
    IEnumerable<IScheduledJobSource> sources,
    IWorkerHostIdentity workerHost,
    ISecretRedactor redactor,
    TimeProvider timeProvider,
    ILogger<ScheduledJobRunner> logger)
{
    public async Task<ScheduledJobRunStatus?> RunAsync(ScheduledJobRunId runId,
        CancellationToken cancellationToken = default)
    {
        ScheduledJobRun? run = await dbContext.ScheduledJobRunRepository.GetByIdAsync(runId, cancellationToken);
        if (run is null || run.Status != ScheduledJobRunStatus.Due) return run?.Status;
        IScheduledJobSource? source = sources.FirstOrDefault(item => item.Kind == run.Kind);
        if (source is null)
        {
            run.Fail($"No runner is registered for {run.Kind} schedules.", null, Now());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return run.Status;
        }

        if (!run.TryClaim(Guid.NewGuid(), workerHost.WorkerHost, Now())) return run.Status;
        try
        {
            // The optimistic concurrency stamp makes a concurrent claim of the same run fail here.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogClaimLost(logger, run.Id.Value, exception);
            return null;
        }

        ScheduledJobOutcome outcome;
        try
        {
            outcome = await source.RunAsync(run, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = ScheduledJobOutcome.Failed(
                "The control plane stopped while this run was running. It is not run again.");
        }
        catch (Exception exception)
        {
            LogRunFailed(logger, run.Kind, run.ScheduleId, exception);
            outcome = ScheduledJobOutcome.Failed(redactor.Redact(exception.Message));
        }

        DateTimeOffset now = Now();
        switch (outcome.Status)
        {
            case ScheduledJobOutcomeStatus.Succeeded:
                run.Succeed(outcome.Message, outcome.ExecutionId, now);
                break;
            case ScheduledJobOutcomeStatus.Skipped:
                run.Skip(outcome.Message, outcome.ExecutionId, now);
                break;
            default:
                run.Fail(outcome.Message, outcome.ExecutionId, now);
                break;
        }

        await dbContext.SaveChangesAsync(CancellationToken.None);
        return run.Status;
    }

    private DateTimeOffset Now()
    {
        return timeProvider.GetUtcNow();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled run {RunId} was claimed by another worker.")]
    private static partial void LogClaimLost(ILogger logger, Guid runId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled {Kind} run of schedule {ScheduleId} failed.")]
    private static partial void LogRunFailed(ILogger logger, ScheduledJobKind kind, Guid scheduleId,
        Exception exception);
}

/// <summary>Hangfire entry point: the per-minute dispatcher pass.</summary>
[JobRetryPolicy(0)]
public sealed class ScheduledJobDispatchJob(ScheduledJobDispatcher dispatcher)
{
    public const string RecurringJobId = "scheduled-jobs:dispatch";

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return dispatcher.DispatchAsync(cancellationToken);
    }
}

/// <summary>Hangfire entry point for one ledger run (carries only the run ID). Never retried automatically.</summary>
[JobRetryPolicy(0)]
public sealed class RunScheduledJobOccurrenceJob(ScheduledJobRunner runner)
{
    public Task RunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return runner.RunAsync(new ScheduledJobRunId(runId), cancellationToken);
    }
}

public static class SchedulingRecurringJobs
{
    public static void Register(IRecurringJobScheduler scheduler)
    {
        scheduler.AddOrUpdate<ScheduledJobDispatchJob>(ScheduledJobDispatchJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None), "* * * * *");
    }
}

/// <summary>Started ledger runs on a worker host (OPS-02 draining waits for them).</summary>
public sealed class ScheduledJobRunsInFlightProbe(IVesselDbContext dbContext) : IInFlightWorkProbe
{
    public string Name => "scheduled job runs";

    public Task<int> CountInFlightAsync(string workerHost, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(dbContext.ScheduledJobRuns.Count(run =>
            run.Status == ScheduledJobRunStatus.Started && run.WorkerHost == workerHost));
    }
}

/// <summary>Ledger history queries (instance administrators; per-schedule queries are authorized by their feature).</summary>
public sealed class ScheduledJobRunQueryService(
    IVesselDbContext dbContext,
    Authorization.VesselAuthorizationService authorization)
{
    public const int MaxPageSize = 100;

    public PagedScheduledJobRuns ListForSchedule(ScheduledJobKind kind, Guid scheduleId, int page, int pageSize)
    {
        return Page(dbContext.ScheduledJobRuns.Where(run => run.Kind == kind && run.ScheduleId == scheduleId), page,
            pageSize);
    }

    public PagedScheduledJobRuns ListRecent(Domain.UserId actorUserId, ScheduledJobKind? kind,
        ScheduledJobRunStatus? status, int page, int pageSize)
    {
        if (!authorization.IsInstanceManager(actorUserId))
            throw new UnauthorizedAccessException("The scheduled job ledger requires an instance administrator.");
        IQueryable<ScheduledJobRun> query = dbContext.ScheduledJobRuns;
        if (kind is { } k) query = query.Where(run => run.Kind == k);
        if (status is { } s) query = query.Where(run => run.Status == s);
        return Page(query, page, pageSize);
    }

    private static PagedScheduledJobRuns Page(IQueryable<ScheduledJobRun> query, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var total = query.Count();
        ScheduledJobRunSummary[] items = query.OrderByDescending(run => run.ScheduledFor)
            .ThenByDescending(run => run.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsEnumerable()
            .Select(ToSummary)
            .ToArray();
        return new PagedScheduledJobRuns(items, page, pageSize, total);
    }

    public static ScheduledJobRunSummary ToSummary(ScheduledJobRun run)
    {
        return new ScheduledJobRunSummary(run.Id.Value, run.Kind, run.ScheduleId, run.ScheduledFor, run.Trigger,
            run.Status, run.EnqueuedAt, run.StartedAt, run.FinishedAt, run.WorkerHost, run.Message, run.ExecutionId);
    }
}
