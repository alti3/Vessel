using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Jobs;
using Vessel.Application.Operations.ControlPlaneBackups;
using Vessel.Application.Operations.Draining;
using Vessel.Application.Operations.Updates;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Operations;

namespace Vessel.Application.Operations.Upgrades;

public sealed record BeginUpgradeRequest(string TargetVersion, bool SkipBackup = false, int? DrainTimeoutSeconds = null);

/// <summary>A step reported by the Phase 14 CLI (Swapping, Migrating, Verifying, Succeeded, Failed, RolledBack).</summary>
public sealed record ReportUpgradeRequest(UpgradeRunStatus Status, string? Message = null);

public sealed record UpgradeRunSummary(
    Guid Id,
    string FromVersion,
    string TargetVersion,
    UpdateChannel Channel,
    UpgradeRunStatus Status,
    string Message,
    Guid? StartedByUserId,
    string WorkerHost,
    Guid? PreUpdateBackupId,
    DateTimeOffset StartedAt,
    DateTimeOffset HeartbeatAt,
    DateTimeOffset? FinishedAt);

public sealed record UpgradeStatus(UpgradeRunSummary? Current, UpgradeRunSummary? Latest, ControlPlaneDrainState Drain);

/// <summary>Another upgrade is in progress (single-flight).</summary>
public sealed class UpgradeInProgressException()
    : InvalidOperationException("Another update of Vessel is already in progress.");

/// <summary>
///     Prepares and tracks control-plane upgrades (OPS-02). Single-flight: a distributed lock (<see cref="LockKey" />)
///     serializes preparation and at most one non-terminal <see cref="UpgradeRun" /> exists (a run without heartbeat for
///     <see cref="UpgradeRun.StaleAfter" /> is failed as abandoned). Preparation dumps the database first when
///     <see cref="InstanceBackupSettings.BeforeUpdate" /> is on and fails closed, then drains running work. The binary
///     swap, migrations, health check and rollback belong to the Phase 14 <c>vessel self-update</c> CLI, which reports
///     each step back through <see cref="ReportAsync" /> / <see cref="ReportFromHostAsync" />.
/// </summary>
public sealed class UpgradeCoordinator(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    ControlPlaneBackupService backups,
    DrainService drain,
    UpdatePolicyService policies,
    IVesselVersionProvider versionProvider,
    IDistributedLockManager locks,
    IWorkerHostIdentity workerHost,
    PlatformNotificationPublisher notifications,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public const string LockKey = "vessel:self-update";

    public const string BackupFailedMessage = "The database backup failed, so the update was not started.";

    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan MaxDrainTimeout = TimeSpan.FromHours(2);

    private static readonly TimeSpan LockLease = TimeSpan.FromHours(3);

    public async Task<UpgradeStatus> GetStatusAsync(UserId actorUserId, CancellationToken cancellationToken = default)
    {
        UpdatePolicyService.RequireInstanceManager(authorization, actorUserId);
        await FailStaleRunsAsync(cancellationToken);
        UpgradeRun? latest = dbContext.UpgradeRuns.OrderByDescending(run => run.CreatedAt).FirstOrDefault();
        UpgradeRun? current = ActiveRuns().FirstOrDefault();
        return new UpgradeStatus(current is null ? null : ToSummary(current), latest is null ? null : ToSummary(latest),
            ControlPlaneDrainGate.Read(dbContext));
    }

    /// <summary>Instance-administrator entry point (API/UI).</summary>
    public Task<UpgradeRunSummary> BeginAsync(UserId actorUserId, BeginUpgradeRequest request, string? correlationId,
        CancellationToken cancellationToken = default)
    {
        UpdatePolicyService.RequireInstanceManager(authorization, actorUserId);
        return BeginCoreAsync(actorUserId, request, correlationId, cancellationToken);
    }

    /// <summary>
    ///     Entry point of the in-process <c>vessel self-update</c> CLI, which runs as the service user on the host (that
    ///     access is already host-root equivalent). Never expose this through HTTP.
    /// </summary>
    public Task<UpgradeRunSummary> BeginFromHostAsync(BeginUpgradeRequest request,
        CancellationToken cancellationToken = default)
    {
        return BeginCoreAsync(null, request, null, cancellationToken);
    }

    public async Task<UpgradeRunSummary> ReportAsync(UserId actorUserId, UpgradeRunId runId,
        ReportUpgradeRequest request, string? correlationId, CancellationToken cancellationToken = default)
    {
        UpdatePolicyService.RequireInstanceManager(authorization, actorUserId);
        return await ReportCoreAsync(actorUserId, runId, request, correlationId, cancellationToken);
    }

    /// <summary>Step report of the in-process CLI (see <see cref="BeginFromHostAsync" />).</summary>
    public Task<UpgradeRunSummary> ReportFromHostAsync(UpgradeRunId runId, ReportUpgradeRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReportCoreAsync(null, runId, request, null, cancellationToken);
    }

    /// <summary>Abandons a run that has not swapped yet and accepts deployments again.</summary>
    public async Task<UpgradeRunSummary> AbortAsync(UserId actorUserId, UpgradeRunId runId, string? reason,
        string? correlationId, CancellationToken cancellationToken = default)
    {
        UpdatePolicyService.RequireInstanceManager(authorization, actorUserId);
        UpgradeRun run = await GetRunAsync(runId, cancellationToken);
        if (run.IsTerminal) throw new InvalidOperationException("The upgrade already finished.");
        if (run.Status >= UpgradeRunStatus.Swapping)
            throw new InvalidOperationException(
                "The binary swap already started; report the outcome (succeeded, failed or rolled back) instead.");
        run.Advance(UpgradeRunStatus.Aborted, string.IsNullOrWhiteSpace(reason) ? "Aborted by an administrator." : reason,
            Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        await drain.ReleaseForUpgradeAsync(cancellationToken);
        await auditWriter.RecordAsync(null, actorUserId, OperationsAuditActions.UpgradeAborted,
            new AuditTarget("upgrade_run", run.Id.ToString()), correlationId, new Dictionary<string, object?>(),
            cancellationToken);
        await notifications.PublishAsync(PlatformNotifications.UpdateFinished(run.Id.Value, run.FromVersion,
            run.TargetVersion, false, run.Status.ToString()), cancellationToken);
        return ToSummary(run);
    }

    private async Task<UpgradeRunSummary> BeginCoreAsync(UserId? actorUserId, BeginUpgradeRequest request,
        string? correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = (request.TargetVersion ?? string.Empty).Trim();
        if (target.Length is 0 or > 128) throw new DomainException("A target version (1-128 characters) is required.");
        TimeSpan drainTimeout = request.DrainTimeoutSeconds is { } seconds
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, (int)MaxDrainTimeout.TotalSeconds))
            : DefaultDrainTimeout;

        await using DistributedLockHandle? handle =
            await locks.TryAcquireAsync(LockKey, LockLease, TimeSpan.Zero, cancellationToken);
        if (handle is null) throw new UpgradeInProgressException();
        await FailStaleRunsAsync(cancellationToken);
        if (ActiveRuns().Any()) throw new UpgradeInProgressException();

        UpdatePolicy policy = await policies.LoadAsync(cancellationToken);
        var run = UpgradeRun.Start(versionProvider.CurrentVersion, target, policy.Channel, actorUserId,
            workerHost.WorkerHost, Now());
        await dbContext.UpgradeRunRepository.AddAsync(run, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (string.Equals(policy.AutoUpdateTargetVersion, target, StringComparison.Ordinal))
            await policies.SaveAsync(policy with { AutoUpdateStartedAt = Now() }, cancellationToken);
        await auditWriter.RecordAsync(null, actorUserId, OperationsAuditActions.UpgradeStarted,
            new AuditTarget("upgrade_run", run.Id.ToString()), correlationId,
            new Dictionary<string, object?>
            {
                ["fromVersion"] = run.FromVersion,
                ["targetVersion"] = run.TargetVersion,
                ["skipBackup"] = request.SkipBackup
            }, cancellationToken);
        await notifications.PublishAsync(
            PlatformNotifications.UpdateStarted(run.Id.Value, run.FromVersion, run.TargetVersion), cancellationToken);

        if (backups.LoadSettings().BeforeUpdate && !request.SkipBackup)
        {
            run.Advance(UpgradeRunStatus.BackingUp, "Backing up the Vessel database.", Now());
            await dbContext.SaveChangesAsync(cancellationToken);
            ControlPlaneBackup backup;
            try
            {
                backup = await backups.RunPreUpdateAsync(actorUserId, cancellationToken);
            }
            catch (ControlPlaneBackupInProgressException)
            {
                return await FailAsync(run, $"{BackupFailedMessage} Another backup was still running.",
                    cancellationToken);
            }

            run.AttachBackup(backup.Id, Now());
            if (backup.Status != ControlPlaneBackupStatus.Succeeded)
                return await FailAsync(run, $"{BackupFailedMessage} {backup.FailureReason}".Trim(), cancellationToken);
        }

        run.Advance(UpgradeRunStatus.Draining, "Waiting for running work to finish.", Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        await drain.StartForUpgradeAsync($"Updating Vessel to {target}.", run.Id.Value, cancellationToken);
        if (!await drain.WaitForIdleAsync(drainTimeout, cancellationToken: cancellationToken))
        {
            await drain.ReleaseForUpgradeAsync(CancellationToken.None);
            return await FailAsync(run,
                $"Running work did not finish within {drainTimeout.TotalMinutes:0.#} minutes, so the update was not started.",
                cancellationToken);
        }

        run.Advance(UpgradeRunStatus.ReadyForSwap,
            "Ready for the binary swap. New deployments are paused until the update finishes.", Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToSummary(run);
    }

    private async Task<UpgradeRunSummary> ReportCoreAsync(UserId? actorUserId, UpgradeRunId runId,
        ReportUpgradeRequest request, string? correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Status is UpgradeRunStatus.Pending or UpgradeRunStatus.BackingUp or UpgradeRunStatus.Draining
            or UpgradeRunStatus.ReadyForSwap or UpgradeRunStatus.Aborted)
            throw new DomainException("Only swap, migration, verification and outcome steps can be reported.");
        UpgradeRun run = await GetRunAsync(runId, cancellationToken);
        if (run.Status < UpgradeRunStatus.ReadyForSwap && !run.IsTerminal)
            throw new InvalidOperationException("The upgrade is not ready for the binary swap yet.");
        run.Advance(request.Status, request.Message, Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(null, actorUserId, OperationsAuditActions.UpgradeReported,
            new AuditTarget("upgrade_run", run.Id.ToString()), correlationId,
            new Dictionary<string, object?> { ["status"] = run.Status.ToString() }, cancellationToken);
        if (run.IsTerminal)
        {
            await drain.ReleaseForUpgradeAsync(cancellationToken);
            await notifications.PublishAsync(PlatformNotifications.UpdateFinished(run.Id.Value, run.FromVersion,
                    run.TargetVersion, run.Status == UpgradeRunStatus.Succeeded, run.Status.ToString()),
                cancellationToken);
        }

        return ToSummary(run);
    }

    private async Task<UpgradeRunSummary> FailAsync(UpgradeRun run, string message, CancellationToken cancellationToken)
    {
        run.Advance(UpgradeRunStatus.Failed, message, Now());
        await dbContext.SaveChangesAsync(CancellationToken.None);
        await notifications.PublishAsync(PlatformNotifications.UpdateFinished(run.Id.Value, run.FromVersion,
            run.TargetVersion, false, run.Status.ToString()), CancellationToken.None);
        return ToSummary(run);
    }

    private async Task FailStaleRunsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = Now();
        UpgradeRun[] stale = ActiveRuns().AsEnumerable().Where(run => run.IsStale(now)).ToArray();
        if (stale.Length == 0) return;
        foreach (UpgradeRun run in stale)
            run.Advance(UpgradeRunStatus.Failed,
                $"No progress was reported for {UpgradeRun.StaleAfter.TotalMinutes:0} minutes; the upgrade was abandoned.",
                now);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (stale.Any(run => run.Status < UpgradeRunStatus.Swapping))
            await drain.ReleaseForUpgradeAsync(cancellationToken);
    }

    private IQueryable<UpgradeRun> ActiveRuns()
    {
        return dbContext.UpgradeRuns.Where(run => run.Status != UpgradeRunStatus.Succeeded &&
                                                  run.Status != UpgradeRunStatus.Failed &&
                                                  run.Status != UpgradeRunStatus.RolledBack &&
                                                  run.Status != UpgradeRunStatus.Aborted);
    }

    private async Task<UpgradeRun> GetRunAsync(UpgradeRunId runId, CancellationToken cancellationToken)
    {
        return await dbContext.UpgradeRunRepository.GetByIdAsync(runId, cancellationToken)
               ?? throw new KeyNotFoundException("Upgrade run was not found.");
    }

    private DateTimeOffset Now()
    {
        return timeProvider.GetUtcNow();
    }

    public static UpgradeRunSummary ToSummary(UpgradeRun run)
    {
        return new UpgradeRunSummary(run.Id.Value, run.FromVersion, run.TargetVersion, run.Channel, run.Status,
            run.Message, run.StartedByUserId?.Value, run.WorkerHost, run.PreUpdateBackupId?.Value, run.CreatedAt,
            run.HeartbeatAt, run.FinishedAt);
    }
}
