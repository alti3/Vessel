using System.Reflection;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Vessel.Application.Jobs;

namespace Vessel.Infrastructure.Jobs;

/// <summary>
///     Replaces Hangfire's global <see cref="AutomaticRetryAttribute" /> (10 retries for every job) with a per-job policy:
///     jobs marked with <see cref="JobRetryPolicyAttribute" /> get its attempt count (0 = a failure stays failed),
///     every other job keeps the Hangfire default. Scheduled occurrences and scheduled tasks record their own outcome,
///     so an automatic retry would run the backup or command a second time (NTF-08).
/// </summary>
public sealed class VesselRetryPolicyFilter : JobFilterAttribute, IElectStateFilter, IApplyStateFilter
{
    public const int DefaultAttempts = 10;

    private readonly AutomaticRetryAttribute _default = new() { Attempts = DefaultAttempts };

    public void OnStateElection(ElectStateContext context)
    {
        For(context.BackgroundJob.Job).OnStateElection(context);
    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        For(context.BackgroundJob.Job).OnStateApplied(context, transaction);
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        For(context.BackgroundJob.Job).OnStateUnapplied(context, transaction);
    }

    /// <summary>The retry attempts configured for a job method (attribute on the method, then on its class).</summary>
    public static int AttemptsFor(Type? type, MethodInfo? method)
    {
        JobRetryPolicyAttribute? policy = method?.GetCustomAttribute<JobRetryPolicyAttribute>() ??
                                          type?.GetCustomAttribute<JobRetryPolicyAttribute>();
        return policy?.Attempts ?? DefaultAttempts;
    }

    /// <summary>Removes Hangfire's default retry filter and installs this one (idempotent).</summary>
    public static void Install(JobFilterCollection filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        filters.Remove<AutomaticRetryAttribute>();
        filters.Remove<VesselRetryPolicyFilter>();
        filters.Add(new VesselRetryPolicyFilter());
    }

    private AutomaticRetryAttribute For(Job? job)
    {
        var attempts = AttemptsFor(job?.Type, job?.Method);
        return attempts == DefaultAttempts
            ? _default
            : new AutomaticRetryAttribute { Attempts = attempts, OnAttemptsExceeded = AttemptsExceededAction.Fail };
    }
}
