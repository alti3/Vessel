using System.Linq.Expressions;

namespace Vessel.Application.Jobs;

public sealed class UnavailableBackgroundJobDispatcher : IBackgroundJobDispatcher
{
    public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall)
    {
        throw new InvalidOperationException(
            "Background job dispatch is unavailable because Hangfire storage is disabled.");
    }

    public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay)
    {
        throw new InvalidOperationException(
            "Background job dispatch is unavailable because Hangfire storage is disabled.");
    }
}

public sealed class UnavailableRecurringJobScheduler : IRecurringJobScheduler
{
    public void AddOrUpdate<TJob>(
        string recurringJobId,
        Expression<Func<TJob, Task>> methodCall,
        string cronExpression)
    {
        throw new InvalidOperationException(
            "Background job scheduling is unavailable because Hangfire storage is disabled.");
    }
}
