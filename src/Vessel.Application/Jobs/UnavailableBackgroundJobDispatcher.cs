using System.Linq.Expressions;

namespace Vessel.Application.Jobs;

public sealed class UnavailableBackgroundJobDispatcher : IBackgroundJobDispatcher
{
    public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall)
    {
        throw new BackgroundJobsUnavailableException(
            "Background job dispatch is unavailable because Hangfire storage is disabled.");
    }

    public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay)
    {
        throw new BackgroundJobsUnavailableException(
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
        throw BackgroundJobsUnavailableException.ForRecurringJob(
            recurringJobId,
            methodCall.ToString(),
            cronExpression);
    }
}

public sealed class BackgroundJobsUnavailableException : InvalidOperationException
{
    public BackgroundJobsUnavailableException()
    {
    }

    public BackgroundJobsUnavailableException(string message)
        : base(message)
    {
    }

    public BackgroundJobsUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private BackgroundJobsUnavailableException(
        string message,
        string recurringJobId,
        string methodCall,
        string cronExpression)
        : base(message)
    {
        RecurringJobId = recurringJobId;
        MethodCall = methodCall;
        CronExpression = cronExpression;
    }

    public string? RecurringJobId { get; }

    public string? MethodCall { get; }

    public string? CronExpression { get; }

    public static BackgroundJobsUnavailableException ForRecurringJob(
        string recurringJobId,
        string methodCall,
        string cronExpression)
    {
        return new BackgroundJobsUnavailableException(
            "Recurring job AddOrUpdate was not executed because Hangfire storage is disabled. "
            + $"RecurringJobId: '{recurringJobId}'. MethodCall: '{methodCall}'. CronExpression: '{cronExpression}'.",
            recurringJobId,
            methodCall,
            cronExpression);
    }
}
