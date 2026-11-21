using System.Linq.Expressions;

namespace Vessel.Application.Jobs;

public interface IBackgroundJobDispatcher
{
    string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall);

    string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay);
}

public interface IRecurringJobScheduler
{
    void AddOrUpdate<TJob>(
        string recurringJobId,
        Expression<Func<TJob, Task>> methodCall,
        string cronExpression);

    /// <summary>Removes a recurring job (no-op when it does not exist), for example when a schedule is disabled.</summary>
    void RemoveIfExists(string recurringJobId)
    {
    }
}
