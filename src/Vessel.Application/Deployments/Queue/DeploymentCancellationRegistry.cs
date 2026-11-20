using System.Collections.Concurrent;
using Vessel.Domain;

namespace Vessel.Application.Deployments.Queue;

/// <summary>
///     Lets a cancellation (user request or a superseding deployment) stop a running deployment's build steps right
///     away instead of at the next checkpoint. Runs register while they are before their deploy phase; the registry is
///     in-process (the modular monolith runs Hangfire workers in the same process). Runs in another process still
///     observe the persisted cancel request at their next checkpoint.
/// </summary>
public interface IDeploymentCancellationRegistry
{
    /// <summary>Registers a run; the returned scope's token is cancelled by <see cref="Cancel" /> or the job token.</summary>
    DeploymentCancellationScope Register(DeploymentId deploymentId, CancellationToken jobToken);

    /// <summary>Signals the running deployment, if it runs in this process. Returns true when a run was signalled.</summary>
    bool Cancel(DeploymentId deploymentId);
}

public sealed class DeploymentCancellationScope : IDisposable
{
    private readonly Action _unregister;
    private readonly CancellationTokenSource _source;

    internal DeploymentCancellationScope(CancellationTokenSource source, Action unregister)
    {
        _source = source;
        _unregister = unregister;
    }

    public CancellationToken Token => _source.Token;

    public bool IsCancellationRequested => _source.IsCancellationRequested;

    internal void Cancel()
    {
        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run already finished.
        }
    }

    public void Dispose()
    {
        _unregister();
        _source.Dispose();
    }
}

public sealed class DeploymentCancellationRegistry : IDeploymentCancellationRegistry
{
    private readonly ConcurrentDictionary<DeploymentId, DeploymentCancellationScope> _runs = new();

    public DeploymentCancellationScope Register(DeploymentId deploymentId, CancellationToken jobToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(jobToken);
        DeploymentCancellationScope? scope = null;
        scope = new DeploymentCancellationScope(source,
            () => _runs.TryRemove(new KeyValuePair<DeploymentId, DeploymentCancellationScope>(deploymentId, scope!)));
        _runs[deploymentId] = scope;
        return scope;
    }

    public bool Cancel(DeploymentId deploymentId)
    {
        if (!_runs.TryGetValue(deploymentId, out DeploymentCancellationScope? scope)) return false;
        scope.Cancel();
        return true;
    }
}
