using System.Linq.Expressions;

namespace Vessel.Application.Jobs;

public interface IBackgroundJobDispatcher
{
    string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall);

    string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay);
}
