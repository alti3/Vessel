using System.Linq.Expressions;
using Hangfire;
using Vessel.Application.Jobs;

namespace Vessel.Infrastructure.Jobs;

public sealed class HangfireBackgroundJobDispatcher(IBackgroundJobClient backgroundJobs) : IBackgroundJobDispatcher
{
    public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall) =>
        backgroundJobs.Enqueue(methodCall);

    public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay) =>
        backgroundJobs.Schedule(methodCall, delay);
}
