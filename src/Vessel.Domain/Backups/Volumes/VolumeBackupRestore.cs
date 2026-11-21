using Vessel.Domain.Common;

namespace Vessel.Domain.Backups;

public enum VolumeRestoreStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}

/// <summary>
///     One restore of a volume backup archive into its volume or directory, run as a background job. The containers that
///     mount the target are stopped for the restore and recorded first, so the recovery job can start them again if the
///     worker dies.
/// </summary>
public sealed class VolumeBackupRestore : Entity<VolumeBackupRestoreId>
{
    public const int MaxLogLength = 64 * 1024;

    private const string TruncatedLogMarker = "[earlier log lines truncated]\n";

    private VolumeBackupRestore()
    {
    }

    private VolumeBackupRestore(VolumeBackupRestoreId id, TeamId teamId, VolumeBackupExecutionId executionId,
        UserId? requestedByUserId, bool replaceExisting, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        VolumeBackupExecutionId = executionId;
        RequestedByUserId = requestedByUserId;
        ReplaceExisting = replaceExisting;
    }

    public TeamId TeamId { get; private set; }

    public VolumeBackupExecutionId VolumeBackupExecutionId { get; private set; }

    public UserId? RequestedByUserId { get; private set; }

    /// <summary>Empty the target before extracting (otherwise files are merged over the existing content).</summary>
    public bool ReplaceExisting { get; private set; }

    public VolumeRestoreStatus Status { get; private set; } = VolumeRestoreStatus.Queued;

    public string Log { get; private set; } = string.Empty;

    public string? FailureReason { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public string? StoppedContainerIds { get; private set; }

    public bool StopRecoveryPending { get; private set; }

    public int RecoveryAttempts { get; private set; }

    public string? RecoveryError { get; private set; }

    public bool RecoveryNeedsAttention { get; private set; }

    public bool IsActive => Status is VolumeRestoreStatus.Queued or VolumeRestoreStatus.Running;

    public IReadOnlyList<string> StoppedContainers => VolumeBackupExecution.SplitContainers(StoppedContainerIds);

    public static VolumeBackupRestore Queue(TeamId teamId, VolumeBackupExecutionId executionId,
        UserId? requestedByUserId, bool replaceExisting, DateTimeOffset now)
    {
        return new VolumeBackupRestore(VolumeBackupRestoreId.New(), teamId, executionId, requestedByUserId,
            replaceExisting, now);
    }

    public void Start(DateTimeOffset now)
    {
        if (Status != VolumeRestoreStatus.Queued) throw new DomainException("Only queued restores can be started.");
        Status = VolumeRestoreStatus.Running;
        StartedAt = now;
        Touch(now);
    }

    public void RecordContainersToStop(IReadOnlyCollection<string> containerIds, DateTimeOffset now)
    {
        StoppedContainerIds = VolumeBackupExecution.JoinContainers(containerIds);
        StopRecoveryPending = StoppedContainerIds is not null;
        Touch(now);
    }

    public void RecordContainersRestarted(DateTimeOffset now)
    {
        StopRecoveryPending = false;
        RecoveryError = null;
        RecoveryNeedsAttention = false;
        Touch(now);
    }

    public void RecordRecoveryFailure(string error, DateTimeOffset now)
    {
        RecoveryAttempts++;
        RecoveryError = VolumeBackupExecution.Truncate(error, 1000);
        if (RecoveryAttempts >= VolumeBackupExecution.MaxRecoveryAttempts) RecoveryNeedsAttention = true;
        Touch(now);
    }

    /// <summary>Appends a (redacted) line, keeping the newest <see cref="MaxLogLength" /> characters.</summary>
    public void AppendLog(string line, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(line)) return;
        var combined = Log + $"[{now:O}] {line.TrimEnd()}\n";
        if (combined.Length > MaxLogLength)
            combined = TruncatedLogMarker + combined[^(MaxLogLength - TruncatedLogMarker.Length)..];
        Log = combined;
        Touch(now);
    }

    public void Succeed(DateTimeOffset now)
    {
        if (Status != VolumeRestoreStatus.Running) throw new DomainException("Only running restores can succeed.");
        Status = VolumeRestoreStatus.Succeeded;
        FinishedAt = now;
        Touch(now);
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        if (!IsActive) throw new DomainException($"Cannot fail a restore from terminal status {Status}.");
        FailureReason = VolumeBackupExecution.Truncate(string.IsNullOrWhiteSpace(reason) ? "Restore failed." : reason,
            1000);
        Status = VolumeRestoreStatus.Failed;
        FinishedAt = now;
        Touch(now);
    }
}
