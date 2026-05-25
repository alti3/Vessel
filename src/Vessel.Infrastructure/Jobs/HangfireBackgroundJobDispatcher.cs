using System.Linq.Expressions;
using Hangfire;
using Hangfire.Common;
using Vessel.Application.Jobs;

namespace Vessel.Infrastructure.Jobs;

public sealed class HangfireBackgroundJobDispatcher(IBackgroundJobClient backgroundJobs) : IBackgroundJobDispatcher
{
    public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall)
    {
        return backgroundJobs.Enqueue(methodCall);
    }

    public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay)
    {
        return backgroundJobs.Schedule(methodCall, delay);
    }
}

public sealed class HangfireRecurringJobScheduler(IRecurringJobManager recurringJobs) : IRecurringJobScheduler
{
    public void AddOrUpdate<TJob>(
        string recurringJobId,
        Expression<Func<TJob, Task>> methodCall,
        string cronExpression)
    {
        recurringJobs.AddOrUpdate(
            recurringJobId,
            Job.FromExpression(methodCall),
            cronExpression,
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc
            });
    }
}
