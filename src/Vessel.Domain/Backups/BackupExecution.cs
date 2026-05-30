using Vessel.Domain.Common;

namespace Vessel.Domain.Backups;

public sealed class BackupExecution : Entity<BackupExecutionId>
{
    private BackupExecution()
    {
    }

    private BackupExecution(
        BackupExecutionId id,
        TeamId teamId,
        DatabaseResourceId databaseResourceId,
        BackupScheduleId? scheduleId,
        BackupStorageKind storageKind,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        DatabaseResourceId = databaseResourceId;
        ScheduleId = scheduleId;
        StorageKind = storageKind;
    }

    public TeamId TeamId { get; private set; }

    public DatabaseResourceId DatabaseResourceId { get; private set; }

    public BackupScheduleId? ScheduleId { get; private set; }

    public BackupExecutionStatus Status { get; private set; } = BackupExecutionStatus.Queued;

    public BackupStorageKind StorageKind { get; private set; }

    public string? ArtifactBucket { get; private set; }

    public string? ArtifactKey { get; private set; }

    public long? SizeBytes { get; private set; }

    public string? ChecksumSha256 { get; private set; }

    public string? FailureReason { get; private set; }

    public string? LastRestoreFailureReason { get; private set; }

    public DateTimeOffset? LastRestoreFailedAt { get; private set; }

    public bool Protected { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public static BackupExecution Queue(
        TeamId teamId,
        DatabaseResourceId databaseResourceId,
        BackupScheduleId? scheduleId,
        BackupStorageKind storageKind,
        DateTimeOffset now)
    {
        return new BackupExecution(BackupExecutionId.New(), teamId, databaseResourceId, scheduleId, storageKind, now);
    }

    public void Start(DateTimeOffset now)
    {
        if (Status != BackupExecutionStatus.Queued)
            throw new DomainException("Only queued backups can be started.");
        Status = BackupExecutionStatus.Running;
        StartedAt = now;
        Touch(now);
    }

    public void Succeed(string bucket, string key, long sizeBytes, string checksumSha256, DateTimeOffset now)
    {
        if (Status != BackupExecutionStatus.Running)
            throw new DomainException("Only running backups can succeed.");
        ArtifactBucket = DomainValidation.Required(bucket, nameof(bucket), 160);
        ArtifactKey = DomainValidation.Required(key, nameof(key), 512);
        if (sizeBytes < 0) throw new DomainException("Backup size cannot be negative.");
        SizeBytes = sizeBytes;
        ChecksumSha256 = DomainValidation.Required(checksumSha256, nameof(checksumSha256), 128);
        Status = BackupExecutionStatus.Succeeded;
        FinishedAt = now;
        Touch(now);
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        if (IsTerminal(Status))
            throw new DomainException($"Cannot fail backup execution from terminal status {Status}.");

        FailureReason = DomainValidation.Required(reason, nameof(reason), 1000);
        Status = Status == BackupExecutionStatus.RestoreValidated
            ? BackupExecutionStatus.RestoreFailed
            : BackupExecutionStatus.Failed;
        FinishedAt = now;
        Touch(now);
    }

    public void MarkPruned(DateTimeOffset now)
    {
        if (Protected) throw new DomainException("Protected backup artifacts cannot be pruned.");
        Status = BackupExecutionStatus.Pruned;
        Touch(now);
    }

    public void Protect(DateTimeOffset now)
    {
        Protected = true;
        Touch(now);
    }

    public void MarkRestoreValidated(DateTimeOffset now)
    {
        if (Status != BackupExecutionStatus.Succeeded)
            throw new DomainException("Only successful backups can be restored.");
        Status = BackupExecutionStatus.RestoreValidated;
        Touch(now);
    }

    public void MarkRestoreSucceeded(DateTimeOffset now)
    {
        if (Status != BackupExecutionStatus.RestoreValidated)
            throw new DomainException("Restore must be validated before it can complete.");
        Status = BackupExecutionStatus.RestoreSucceeded;
        LastRestoreFailureReason = null;
        LastRestoreFailedAt = null;
        FinishedAt = now;
        Touch(now);
    }

    public void MarkRestoreFailed(string reason, DateTimeOffset now)
    {
        if (Status is not (BackupExecutionStatus.Succeeded or BackupExecutionStatus.RestoreValidated))
            throw new DomainException("Only successful backup artifacts can record restore failures.");
        LastRestoreFailureReason = DomainValidation.Required(reason, nameof(reason), 1000);
        LastRestoreFailedAt = now;
        Touch(now);
    }

    private static bool IsTerminal(BackupExecutionStatus status)
    {
        return status is BackupExecutionStatus.Succeeded
            or BackupExecutionStatus.Failed
            or BackupExecutionStatus.Pruned
            or BackupExecutionStatus.RestoreSucceeded
            or BackupExecutionStatus.RestoreFailed;
    }
}
