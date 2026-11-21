using Vessel.Domain.Common;

namespace Vessel.Domain.Backups;

public sealed class BackupExecution : Entity<BackupExecutionId>
{
    public const int MaxDatabaseNameLength = 63;

    private readonly List<BackupArtifactReplica> _replicas = [];

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

    /// <summary>Logical database dumped by this run; <see langword="null" /> for the engine default or a full dump.</summary>
    public string? DatabaseName { get; private set; }

    /// <summary>Artifact format reported by the dump tool, for example <c>postgresql-custom</c>.</summary>
    public string? ArtifactFormat { get; private set; }

    /// <summary>Copies of the artifact on remote backup destinations.</summary>
    public IReadOnlyCollection<BackupArtifactReplica> Replicas => _replicas.AsReadOnly();

    /// <summary>True while the local (control-plane host) copy exists.</summary>
    public bool HasLocalCopy => ArtifactBucket is not null && ArtifactKey is not null;

    public static BackupExecution Queue(
        TeamId teamId,
        DatabaseResourceId databaseResourceId,
        BackupScheduleId? scheduleId,
        BackupStorageKind storageKind,
        DateTimeOffset now,
        string? databaseName = null)
    {
        return new BackupExecution(BackupExecutionId.New(), teamId, databaseResourceId, scheduleId, storageKind, now)
        {
            DatabaseName = databaseName is null ? null : BackupDatabaseSelection.ValidateName(databaseName)
        };
    }

    /// <summary>
    ///     Records that the run was skipped before anything was dumped (another database operation was in progress, or
    ///     the database was not running). Skipped runs are terminal and are not failures.
    /// </summary>
    public void Skip(string reason, DateTimeOffset now)
    {
        if (Status != BackupExecutionStatus.Queued)
            throw new DomainException("Only queued backups can be skipped.");
        FailureReason = DomainValidation.Required(reason, nameof(reason), 1000);
        Status = BackupExecutionStatus.Skipped;
        FinishedAt = now;
        Touch(now);
    }

    /// <summary>Records the artifact produced by the dump, before any copy is stored.</summary>
    public void RecordArtifact(string key, long sizeBytes, string checksumSha256, string format, DateTimeOffset now)
    {
        if (Status != BackupExecutionStatus.Running)
            throw new DomainException("Only running backups can record an artifact.");
        ArtifactKey = DomainValidation.Required(key, nameof(key), 512);
        if (sizeBytes < 0) throw new DomainException("Backup size cannot be negative.");
        SizeBytes = sizeBytes;
        ChecksumSha256 = DomainValidation.Required(checksumSha256, nameof(checksumSha256), 128);
        ArtifactFormat = DomainValidation.Required(format, nameof(format), 64);
        Touch(now);
    }

    /// <summary>Records the outcome of uploading the artifact to one remote destination.</summary>
    public void RecordReplica(BackupDestinationId destinationId, string bucket, bool uploaded, string? message,
        DateTimeOffset now)
    {
        if (Status != BackupExecutionStatus.Running)
            throw new DomainException("Only running backups can record remote copies.");
        _replicas.RemoveAll(replica => replica.BackupDestinationId == destinationId);
        _replicas.Add(BackupArtifactReplica.Create(Id, destinationId, bucket, uploaded, message, now));
        Touch(now);
    }

    /// <summary>
    ///     Completes the run once the artifact is stored. At least one copy (local or remote) must exist.
    /// </summary>
    public void Complete(string? localBucket, DateTimeOffset now)
    {
        if (Status != BackupExecutionStatus.Running)
            throw new DomainException("Only running backups can succeed.");
        if (ArtifactKey is null) throw new DomainException("The backup artifact was not recorded.");
        ArtifactBucket = localBucket is null ? null : DomainValidation.Required(localBucket, nameof(localBucket), 160);
        if (!HasLocalCopy && !_replicas.Any(replica => replica.IsAvailable))
            throw new DomainException("The backup was not stored on any destination.");
        Status = BackupExecutionStatus.Succeeded;
        FinishedAt = now;
        Touch(now);
    }

    /// <summary>Records that the local copy was deleted (retention or manual delete).</summary>
    public void MarkLocalCopyDeleted(DateTimeOffset now)
    {
        ArtifactBucket = null;
        MarkPrunedWhenNoCopiesRemain(now);
    }

    public void MarkReplicaDeleted(BackupDestinationId destinationId, DateTimeOffset now)
    {
        BackupArtifactReplica? replica =
            _replicas.SingleOrDefault(item => item.BackupDestinationId == destinationId);
        if (replica is null) return;
        replica.MarkDeleted(now);
        MarkPrunedWhenNoCopiesRemain(now);
    }

    private void MarkPrunedWhenNoCopiesRemain(DateTimeOffset now)
    {
        if (!HasLocalCopy && !_replicas.Any(replica => replica.IsAvailable) &&
            Status is BackupExecutionStatus.Succeeded or BackupExecutionStatus.RestoreSucceeded
                or BackupExecutionStatus.RestoreFailed)
            Status = BackupExecutionStatus.Pruned;
        Touch(now);
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
        if (Status is not (BackupExecutionStatus.Succeeded or BackupExecutionStatus.Failed
            or BackupExecutionStatus.RestoreFailed or BackupExecutionStatus.RestoreSucceeded))
            throw new DomainException("Only completed backup executions can be pruned.");
        Status = BackupExecutionStatus.Pruned;
        Touch(now);
    }

    public void Protect(DateTimeOffset now)
    {
        Protected = true;
        Touch(now);
    }

    /// <summary>True when the execution holds a complete artifact that can be restored (again).</summary>
    public bool HasRestorableArtifact =>
        Status is (BackupExecutionStatus.Succeeded or BackupExecutionStatus.RestoreValidated
            or BackupExecutionStatus.RestoreSucceeded)
        && ArtifactKey is not null
        && (ArtifactBucket is not null || _replicas.Any(replica => replica.IsAvailable));

    public void MarkRestoreValidated(DateTimeOffset now)
    {
        if (Status == BackupExecutionStatus.RestoreValidated) return;
        if (Status is not (BackupExecutionStatus.Succeeded or BackupExecutionStatus.RestoreSucceeded))
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
        if (Status is not (BackupExecutionStatus.Succeeded or BackupExecutionStatus.RestoreValidated
            or BackupExecutionStatus.RestoreSucceeded))
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
            or BackupExecutionStatus.RestoreFailed
            or BackupExecutionStatus.Skipped;
    }
}

/// <summary>
///     One copy of a backup artifact on a remote destination (upstream <c>database_backup_s3_replicas</c>). The
///     destination reference is cleared when the destination is deleted; the row then documents the lost copy.
/// </summary>
public sealed class BackupArtifactReplica
{
    public const int MaxMessageLength = 1000;

    private BackupArtifactReplica()
    {
    }

    public Guid Id { get; private set; }

    public BackupExecutionId BackupExecutionId { get; private set; }

    public BackupDestinationId? BackupDestinationId { get; private set; }

    public string Bucket { get; private set; } = string.Empty;

    public bool Uploaded { get; private set; }

    public string? Message { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsAvailable => Uploaded && DeletedAt is null && BackupDestinationId is not null;

    internal static BackupArtifactReplica Create(BackupExecutionId executionId, BackupDestinationId destinationId,
        string bucket, bool uploaded, string? message, DateTimeOffset now)
    {
        var trimmed = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        return new BackupArtifactReplica
        {
            Id = Guid.NewGuid(),
            BackupExecutionId = executionId,
            BackupDestinationId = destinationId,
            Bucket = DomainValidation.Required(bucket, nameof(bucket), 63),
            Uploaded = uploaded,
            Message = trimmed is { Length: > MaxMessageLength } ? trimmed[..MaxMessageLength] : trimmed,
            CreatedAt = now
        };
    }

    internal void MarkDeleted(DateTimeOffset now)
    {
        DeletedAt ??= now;
    }
}
