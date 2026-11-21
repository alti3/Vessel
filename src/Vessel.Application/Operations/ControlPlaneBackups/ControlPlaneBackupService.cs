using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Jobs;
using Vessel.Application.Operations.Updates;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Application.Settings;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Operations;

namespace Vessel.Application.Operations.ControlPlaneBackups;

/// <summary>
///     Backups of Vessel's own PostgreSQL database (DATA-13 / OPS-02): on demand by instance administrators, before every
///     upgrade (fail closed, see <see cref="Upgrades.UpgradeCoordinator" />) and on an optional schedule. Each dump is a
///     <c>pg_dump --format=custom</c> file with a SHA-256 checksum file next to it; the newest
///     <see cref="InstanceBackupSettings.RetentionCount" /> dumps are kept per trigger.
/// </summary>
public sealed class ControlPlaneBackupService(
    IVesselDbContext dbContext,
    SystemSettingsStore settings,
    VesselAuthorizationService authorization,
    IControlPlaneDatabaseDumper dumper,
    IDistributedLockManager locks,
    IBackgroundJobDispatcher jobs,
    ISecretRedactor redactor,
    PlatformNotificationPublisher notifications,
    IWorkerHostIdentity workerHost,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public const string SettingKey = "operations.instance_backup";

    public const string LockKey = "operations:control-plane-backup";

    private static readonly TimeSpan LockLease = TimeSpan.FromHours(2);

    // ---- Settings --------------------------------------------------------------------------------------------

    public InstanceBackupSettings GetSettings(UserId actorUserId)
    {
        UpdatePolicyService.RequireInstanceManager(authorization, actorUserId);
        return LoadSettings();
    }

    public InstanceBackupSettings LoadSettings()
    {
        return settings.Get(SettingKey, new InstanceBackupSettings());
    }

    public async Task<InstanceBackupSettings> UpdateSettingsAsync(UserId actorUserId,
        UpdateInstanceBackupSettingsRequest request, string? correlationId,
        CancellationToken cancellationToken = default)
    {
        UpdatePolicyService.RequireInstanceManager(authorization, actorUserId);
        ArgumentNullException.ThrowIfNull(request);
        InstanceBackupSettings next = LoadSettings();
        if (request.Enabled is { } enabled) next = next with { Enabled = enabled };
        if (request.BeforeUpdate is { } beforeUpdate) next = next with { BeforeUpdate = beforeUpdate };
        if (request.Schedule is not null)
            next = next with
            {
                Schedule = request.Schedule.Length == 0 ? null : UpdatePolicyService.ValidateCron(request.Schedule)
            };
        if (request.RetentionCount is { } count)
        {
            if (count is < 1 or > InstanceBackupSettings.MaxRetentionCount)
                throw new DomainException(
                    $"Backup retention must keep between 1 and {InstanceBackupSettings.MaxRetentionCount} dumps.");
            next = next with { RetentionCount = count };
        }

        await settings.SetAsync(SettingKey, next, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(null, actorUserId, OperationsAuditActions.BackupSettingsChanged,
            new AuditTarget("instance_setting", SettingKey), correlationId,
            new Dictionary<string, object?>
            {
                ["enabled"] = next.Enabled,
                ["beforeUpdate"] = next.BeforeUpdate,
                ["schedule"] = next.Schedule,
                ["retentionCount"] = next.RetentionCount
            }, cancellationToken);
        return next;
    }

    // ---- Running ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     Queues an on-demand dump on a background worker (dumps of large databases outlive an HTTP request). The
    ///     result shows up in <see cref="List" />.
    /// </summary>
    public string Queue(UserId actorUserId)
    {
        UpdatePolicyService.RequireInstanceManager(authorization, actorUserId);
        return jobs.Enqueue<RunManualControlPlaneBackupJob>(job =>
            job.RunAsync(actorUserId.Value, CancellationToken.None));
    }

    /// <summary>
    ///     On-demand dump by an instance administrator, in the caller's flow (the in-process <c>vessel</c> CLI and the
    ///     background job). A concurrent dump is rejected (409).
    /// </summary>
    public async Task<ControlPlaneBackupSummary> RunAsync(UserId actorUserId, string? correlationId,
        CancellationToken cancellationToken = default)
    {
        UpdatePolicyService.RequireInstanceManager(authorization, actorUserId);
        ControlPlaneBackup backup = await RunCoreAsync(ControlPlaneBackupTrigger.Manual, actorUserId, TimeSpan.Zero,
            correlationId, cancellationToken);
        return ToSummary(backup);
    }

    /// <summary>
    ///     Pre-update dump. Always runs on this host, whatever the schedule settings are (upstream v4.7.0); waits up to
    ///     a minute for a running dump to finish. The caller checks <see cref="ControlPlaneBackup.Status" />.
    /// </summary>
    public Task<ControlPlaneBackup> RunPreUpdateAsync(UserId? actorUserId, CancellationToken cancellationToken = default)
    {
        return RunCoreAsync(ControlPlaneBackupTrigger.PreUpdate, actorUserId, TimeSpan.FromMinutes(1), null,
            cancellationToken);
    }

    /// <summary>
    ///     Scheduled dump (hooked into the scheduled-job ledger with <see cref="InstanceBackupSettings.Schedule" />).
    ///     Returns <see langword="null" /> when scheduled backups are disabled or another dump is running.
    /// </summary>
    public async Task<ControlPlaneBackupSummary?> RunScheduledAsync(CancellationToken cancellationToken = default)
    {
        InstanceBackupSettings current = LoadSettings();
        if (!current.Enabled || current.Schedule is null) return null;
        try
        {
            return ToSummary(await RunCoreAsync(ControlPlaneBackupTrigger.Scheduled, null, TimeSpan.Zero, null,
                cancellationToken));
        }
        catch (ControlPlaneBackupInProgressException)
        {
            return null;
        }
    }

    private async Task<ControlPlaneBackup> RunCoreAsync(ControlPlaneBackupTrigger trigger, UserId? actorUserId,
        TimeSpan lockWait, string? correlationId, CancellationToken cancellationToken)
    {
        await using DistributedLockHandle? handle =
            await locks.TryAcquireAsync(LockKey, LockLease, lockWait, cancellationToken);
        if (handle is null) throw new ControlPlaneBackupInProgressException();

        DateTimeOffset now = timeProvider.GetUtcNow();
        var backup = ControlPlaneBackup.Start(trigger, actorUserId, workerHost.WorkerHost, now);
        await dbContext.ControlPlaneBackupRepository.AddAsync(backup, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var relativePath =
            $"{ControlPlaneBackup.FolderFor(trigger)}/vessel-{now:yyyyMMdd'T'HHmmss'Z'}-{backup.Id.Value.ToString("N")[..8]}.dump";
        try
        {
            ControlPlaneDumpResult result = await dumper.DumpAsync(new ControlPlaneDumpRequest(relativePath),
                cancellationToken);
            backup.Succeed(result.RelativePath, result.SizeBytes, result.Sha256, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            await ApplyRetentionAsync(trigger, CancellationToken.None);
            await auditWriter.RecordAsync(null, actorUserId, OperationsAuditActions.ControlPlaneBackupCompleted,
                new AuditTarget("control_plane_backup", backup.Id.ToString()), correlationId,
                new Dictionary<string, object?>
                {
                    ["trigger"] = trigger.ToString(),
                    ["sizeBytes"] = result.SizeBytes,
                    ["sha256"] = result.Sha256
                }, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            var reason = exception is ControlPlaneDumpException
                ? redactor.Redact(exception.Message)
                : "The database dump failed unexpectedly. See the Vessel service log for details.";
            backup.Fail(reason, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            await auditWriter.RecordAsync(null, actorUserId, OperationsAuditActions.ControlPlaneBackupFailed,
                new AuditTarget("control_plane_backup", backup.Id.ToString()), correlationId,
                new Dictionary<string, object?> { ["trigger"] = trigger.ToString() }, CancellationToken.None);
            await notifications.PublishAsync(
                PlatformNotifications.ControlPlaneBackupFailed(backup.Id.Value, trigger.ToString()),
                CancellationToken.None);
        }

        return backup;
    }

    private async Task ApplyRetentionAsync(ControlPlaneBackupTrigger trigger, CancellationToken cancellationToken)
    {
        var keep = LoadSettings().RetentionCount;
        ControlPlaneBackup[] expired = dbContext.ControlPlaneBackups
            .Where(backup => backup.Trigger == trigger && backup.Status == ControlPlaneBackupStatus.Succeeded)
            .OrderByDescending(backup => backup.StartedAt)
            .AsEnumerable()
            .Skip(keep)
            .ToArray();
        // Dumps referenced by an upgrade that is still running are never pruned.
        ControlPlaneBackupId?[] protectedIds = dbContext.UpgradeRuns
            .Where(run => run.PreUpdateBackupId != null)
            .AsEnumerable()
            .Where(run => !run.IsTerminal)
            .Select(run => run.PreUpdateBackupId)
            .ToArray();
        foreach (ControlPlaneBackup backup in expired.Where(backup => !protectedIds.Contains(backup.Id)))
        {
            if (backup.RelativePath is not null) dumper.Delete(backup.RelativePath);
            dbContext.ControlPlaneBackupRepository.Remove(backup);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // ---- Listing, download, delete ---------------------------------------------------------------------------

    public IReadOnlyList<ControlPlaneBackupSummary> List(UserId actorUserId)
    {
        UpdatePolicyService.RequireInstanceManager(authorization, actorUserId);
        return dbContext.ControlPlaneBackups
            .OrderByDescending(backup => backup.StartedAt)
            .Take(200)
            .AsEnumerable()
            .Select(ToSummary)
            .ToArray();
    }

    /// <summary>Opens a dump for download. The dump holds every row of Vessel (encrypted secrets included).</summary>
    public async Task<ControlPlaneBackupDownload> OpenDownloadAsync(UserId actorUserId, ControlPlaneBackupId backupId,
        string? correlationId, CancellationToken cancellationToken = default)
    {
        UpdatePolicyService.RequireInstanceManager(authorization, actorUserId);
        ControlPlaneBackup backup = await GetAsync(backupId, cancellationToken);
        if (backup.Status != ControlPlaneBackupStatus.Succeeded || backup.RelativePath is null)
            throw new DomainException("This backup has no downloadable dump.");
        Stream content = dumper.OpenRead(backup.RelativePath);
        await auditWriter.RecordAsync(null, actorUserId, OperationsAuditActions.ControlPlaneBackupDownloaded,
            new AuditTarget("control_plane_backup", backup.Id.ToString()), correlationId,
            new Dictionary<string, object?> { ["sizeBytes"] = backup.SizeBytes }, cancellationToken);
        return new ControlPlaneBackupDownload(content, FileName(backup.RelativePath), backup.SizeBytes);
    }

    public async Task DeleteAsync(UserId actorUserId, ControlPlaneBackupId backupId, string? correlationId,
        CancellationToken cancellationToken = default)
    {
        UpdatePolicyService.RequireInstanceManager(authorization, actorUserId);
        ControlPlaneBackup backup = await GetAsync(backupId, cancellationToken);
        if (backup.Status == ControlPlaneBackupStatus.Running)
            throw new InvalidOperationException("The backup is still running.");
        if (dbContext.UpgradeRuns.Where(run => run.PreUpdateBackupId == backup.Id).AsEnumerable()
            .Any(run => !run.IsTerminal))
            throw new InvalidOperationException("The backup belongs to an upgrade that is still running.");
        if (backup.RelativePath is not null) dumper.Delete(backup.RelativePath);
        dbContext.ControlPlaneBackupRepository.Remove(backup);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(null, actorUserId, OperationsAuditActions.ControlPlaneBackupDeleted,
            new AuditTarget("control_plane_backup", backup.Id.ToString()), correlationId,
            new Dictionary<string, object?> { ["trigger"] = backup.Trigger.ToString() }, cancellationToken);
    }

    public static ControlPlaneBackupSummary ToSummary(ControlPlaneBackup backup)
    {
        return new ControlPlaneBackupSummary(backup.Id.Value, backup.Trigger, backup.Status,
            backup.RelativePath is null ? null : FileName(backup.RelativePath), backup.SizeBytes,
            backup.ChecksumSha256, backup.StartedAt, backup.FinishedAt, backup.FailureReason,
            backup.RequestedByUserId?.Value, backup.WorkerHost);
    }

    private async Task<ControlPlaneBackup> GetAsync(ControlPlaneBackupId backupId, CancellationToken cancellationToken)
    {
        return await dbContext.ControlPlaneBackupRepository.GetByIdAsync(backupId, cancellationToken)
               ?? throw new KeyNotFoundException("Backup was not found.");
    }

    private static string FileName(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash >= 0 ? relativePath[(slash + 1)..] : relativePath;
    }
}

public sealed class ControlPlaneBackupInProgressException()
    : InvalidOperationException("Another backup of the Vessel database is running.");

/// <summary>Hangfire entry point for an on-demand dump; never retried automatically (a retry would dump twice).</summary>
[JobRetryPolicy(0)]
public sealed class RunManualControlPlaneBackupJob(ControlPlaneBackupService service)
{
    public Task RunAsync(Guid actorUserId, CancellationToken cancellationToken = default)
    {
        return service.RunAsync(new UserId(actorUserId), null, cancellationToken);
    }
}
