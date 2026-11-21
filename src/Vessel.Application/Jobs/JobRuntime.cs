namespace Vessel.Application.Jobs;

/// <summary>
///     Retry policy of a background job class or method. Hangfire retries failed jobs ten times by default; a job that
///     records its own outcome (scheduled occurrences, scheduled tasks) must never be re-run automatically, because a
///     retry would execute the command or backup a second time. The Infrastructure job filter reads this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class JobRetryPolicyAttribute(int attempts) : Attribute
{
    /// <summary>Automatic retry attempts after a failure (0 = never retry).</summary>
    public int Attempts { get; } = attempts >= 0 ? attempts : throw new ArgumentOutOfRangeException(nameof(attempts));
}

/// <summary>Identifies the control-plane host that runs background work (recorded on scheduled runs and upgrades).</summary>
public interface IWorkerHostIdentity
{
    string WorkerHost { get; }
}

/// <summary>The machine name, which is stable across restarts of the same host.</summary>
public sealed class MachineWorkerHostIdentity : IWorkerHostIdentity
{
    public string WorkerHost { get; } = Truncate(Environment.MachineName);

    private static string Truncate(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Length <= 128 ? value : value[..128];
    }
}
