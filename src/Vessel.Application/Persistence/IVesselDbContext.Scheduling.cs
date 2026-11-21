using Vessel.Domain.ScheduledTasks;
using Vessel.Domain.Scheduling;

namespace Vessel.Application.Persistence;

/// <summary>Scheduled job ledger and user scheduled tasks (NTF-07/NTF-08).</summary>
public partial interface IVesselDbContext
{
    IQueryable<ScheduledJobRun> ScheduledJobRuns => Enumerable.Empty<ScheduledJobRun>().AsQueryable();

    IQueryable<ScheduledTask> ScheduledTasks => Enumerable.Empty<ScheduledTask>().AsQueryable();

    IQueryable<ScheduledTaskExecution> ScheduledTaskExecutions =>
        Enumerable.Empty<ScheduledTaskExecution>().AsQueryable();

    IRepository<ScheduledJobRun, ScheduledJobRunId> ScheduledJobRunRepository =>
        throw new InvalidOperationException("Scheduled job run persistence is not available.");

    IRepository<ScheduledTask, ScheduledTaskId> ScheduledTaskRepository =>
        throw new InvalidOperationException("Scheduled task persistence is not available.");

    IRepository<ScheduledTaskExecution, ScheduledTaskExecutionId> ScheduledTaskExecutionRepository =>
        throw new InvalidOperationException("Scheduled task execution persistence is not available.");
}
