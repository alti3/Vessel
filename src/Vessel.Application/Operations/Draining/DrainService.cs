using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Jobs;
using Vessel.Application.Operations.Updates;
using Vessel.Application.Persistence;
using Vessel.Application.Settings;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Deployments;

namespace Vessel.Application.Operations.Draining;

/// <summary>Persisted drain state: while active, Vessel accepts no new deployments (OPS-02 job draining).</summary>
public sealed record ControlPlaneDrainState
{
    public bool Active { get; init; }

    public DateTimeOffset? Since { get; init; }

    public string? Reason { get; init; }

    public Guid? UpgradeRunId { get; init; }

    public string? WorkerHost { get; init; }
}

public sealed record InFlightWork(string Name, int Count);

public sealed record DrainStatus(ControlPlaneDrainState State, IReadOnlyList<InFlightWork> InFlight)
{
    public bool Idle => InFlight.All(work => work.Count == 0);
}

/// <summary>A new deployment was refused because the control plane is draining for an upgrade.</summary>
public sealed class ControlPlaneDrainingException()
    : InvalidOperationException(
        "Vessel is preparing an update and does not accept new deployments right now. Try again after the update.");

/// <summary>The admission check consulted by every deployment entry point (manual, API, webhook, preview, rollback).</summary>
public static class ControlPlaneDrainGate
{
    public const string SettingKey = "operations.drain";

    public static ControlPlaneDrainState Read(IVesselDbContext dbContext)
    {
        return new SystemSettingsStore(dbContext, TimeProvider.System).Get(SettingKey, new ControlPlaneDrainState());
    }

    public static void ThrowIfDraining(IVesselDbContext dbContext)
    {
        if (Read(dbContext).Active) throw new ControlPlaneDrainingException();
    }
}

/// <summary>
///     Job draining before an upgrade (upstream v4.6.0 "the upgrade waits for running scheduled jobs on this host"):
///     stop accepting new deployments, then wait (bounded) until every <see cref="IInFlightWorkProbe" /> reports no
///     running work on this worker host. Releasing the drain accepts deployments again; queued deployments resume.
/// </summary>
public sealed class DrainService(
    IVesselDbContext dbContext,
    SystemSettingsStore settings,
    VesselAuthorizationService authorization,
    IEnumerable<IInFlightWorkProbe> probes,
    IWorkerHostIdentity workerHost,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    public async Task<DrainStatus> GetStatusAsync(UserId actorUserId, CancellationToken cancellationToken = default)
    {
        UpdatePolicyService.RequireInstanceManager(authorization, actorUserId);
        return await GetStatusCoreAsync(cancellationToken);
    }

    public async Task<DrainStatus> StartAsync(UserId actorUserId, string? reason, string? correlationId,
        CancellationToken cancellationToken = default)
    {
        UpdatePolicyService.RequireInstanceManager(authorization, actorUserId);
        await StartForUpgradeAsync(string.IsNullOrWhiteSpace(reason) ? "Drain requested by an administrator." : reason,
            null, cancellationToken);
        await auditWriter.RecordAsync(null, actorUserId, OperationsAuditActions.DrainStarted,
            new AuditTarget("instance_setting", ControlPlaneDrainGate.SettingKey), correlationId,
            new Dictionary<string, object?>(), cancellationToken);
        return await GetStatusCoreAsync(cancellationToken);
    }

    public async Task<DrainStatus> ReleaseAsync(UserId actorUserId, string? correlationId,
        CancellationToken cancellationToken = default)
    {
        UpdatePolicyService.RequireInstanceManager(authorization, actorUserId);
        await ReleaseForUpgradeAsync(cancellationToken);
        await auditWriter.RecordAsync(null, actorUserId, OperationsAuditActions.DrainReleased,
            new AuditTarget("instance_setting", ControlPlaneDrainGate.SettingKey), correlationId,
            new Dictionary<string, object?>(), cancellationToken);
        return await GetStatusCoreAsync(cancellationToken);
    }

    /// <summary>System-level start (upgrade coordinator / in-process CLI); callers authorize first.</summary>
    public async Task StartForUpgradeAsync(string reason, Guid? upgradeRunId, CancellationToken cancellationToken)
    {
        var reasonText = reason.Trim();
        await settings.SetAsync(ControlPlaneDrainGate.SettingKey, new ControlPlaneDrainState
        {
            Active = true,
            Since = timeProvider.GetUtcNow(),
            Reason = reasonText.Length <= 500 ? reasonText : reasonText[..500],
            UpgradeRunId = upgradeRunId,
            WorkerHost = workerHost.WorkerHost
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>System-level release (upgrade coordinator / in-process CLI); callers authorize first.</summary>
    public async Task ReleaseForUpgradeAsync(CancellationToken cancellationToken)
    {
        if (!ControlPlaneDrainGate.Read(dbContext).Active) return;
        await settings.SetAsync(ControlPlaneDrainGate.SettingKey, new ControlPlaneDrainState(), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Polls the probes until all report idle or <paramref name="timeout" /> passes.</summary>
    /// <returns><see langword="true" /> when no work is running on this host.</returns>
    public async Task<bool> WaitForIdleAsync(TimeSpan timeout, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset deadline = timeProvider.GetUtcNow() + timeout;
        TimeSpan interval = pollInterval ?? DefaultPollInterval;
        while (true)
        {
            DrainStatus status = await GetStatusCoreAsync(cancellationToken);
            if (status.Idle) return true;
            if (timeProvider.GetUtcNow() >= deadline) return false;
            await Task.Delay(interval, cancellationToken);
        }
    }

    private async Task<DrainStatus> GetStatusCoreAsync(CancellationToken cancellationToken)
    {
        var work = new List<InFlightWork>();
        foreach (IInFlightWorkProbe probe in probes)
            work.Add(new InFlightWork(probe.Name, await probe.CountInFlightAsync(workerHost.WorkerHost,
                cancellationToken)));
        return new DrainStatus(ControlPlaneDrainGate.Read(dbContext), work);
    }
}

/// <summary>
///     Deployments that are running or were handed to a worker (deployments record no worker host, so every running
///     deployment counts). Queued deployments that were not dispatched wait for the restarted control plane.
/// </summary>
public sealed class DeploymentInFlightWorkProbe(IVesselDbContext dbContext) : IInFlightWorkProbe
{
    public string Name => "deployments";

    public Task<int> CountInFlightAsync(string workerHost, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(dbContext.Deployments.Count(deployment =>
            deployment.Status == DeploymentStatus.InProgress ||
            deployment.Status == DeploymentStatus.CancelRequested ||
            (deployment.Status == DeploymentStatus.Queued && deployment.DispatchedAt != null)));
    }
}
