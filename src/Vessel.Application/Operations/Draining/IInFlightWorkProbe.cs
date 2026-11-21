namespace Vessel.Application.Operations.Draining;

/// <summary>
///     Reports work that is still running on one worker host (deployments, scheduled job runs), so an upgrade can wait
///     for it to finish before the control plane restarts (OPS-02 job draining). Implementations are registered as an
///     enumerable service; each one counts only its own kind of work.
/// </summary>
public interface IInFlightWorkProbe
{
    /// <summary>Short display name of the work kind (for example <c>deployments</c>).</summary>
    string Name { get; }

    /// <summary>Number of items of this kind running on <paramref name="workerHost" /> (or anywhere, when the kind has no host).</summary>
    Task<int> CountInFlightAsync(string workerHost, CancellationToken cancellationToken = default);
}
