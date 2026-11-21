using Vessel.Domain.Common;

namespace Vessel.Domain.Backups;

public enum VolumeBackupStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,

    /// <summary>Not archived: another backup or restore of the same target was running, or the target is gone.</summary>
    Skipped = 4,

    /// <summary>Every copy was deleted by retention or by hand.</summary>
    Pruned = 5
}

/// <summary>
///     One run of a volume backup schedule (upstream <c>scheduled_volume_backup_executions</c>). Containers stopped for
///     the run are recorded before they are stopped (<see cref="StoppedContainerIds" />, <see cref="StopRecoveryPending" />)
///     so an interrupted run can be compensated by the recovery job, which retries a bounded number of times and then
///     marks the execution as needing attention.
/// </summary>
public sealed class VolumeBackupExecution : Entity<VolumeBackupExecutionId>
{
    public const int MaxRecoveryAttempts = 5;

    public const int MaxContainerIdsLength = 4000;

    private readonly List<VolumeBackupReplica> _replicas = [];

    private VolumeBackupExecution()
    {
    }

    private VolumeBackupExecution(VolumeBackupExecutionId id, TeamId teamId, ScheduledVolumeBackupId scheduleId,
        bool manual, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        ScheduleId = scheduleId;
        Manual = manual;
    }

    public TeamId TeamId { get; private set; }

    public ScheduledVolumeBackupId ScheduleId { get; private set; }

    public bool Manual { get; private set; }

    public VolumeBackupStatus Status { get; private set; } = VolumeBackupStatus.Queued;

    public string? ArtifactBucket { get; private set; }

    public string? ArtifactKey { get; private set; }

    public long? SizeBytes { get; private set; }

    public string? ChecksumSha256 { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>Non-fatal note, for example "files changed while they were archived" (tar exit code 1).</summary>
    public string? Warning { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public bool Protected { get; private set; }

    /// <summary>Comma-separated IDs of containers this run stopped and must start again.</summary>
    public string? StoppedContainerIds { get; private set; }

    public bool StopRecoveryPending { get; private set; }

    public int RecoveryAttempts { get; private set; }

    public DateTimeOffset? RecoveryLastAttemptAt { get; private set; }

    public string? RecoveryError { get; private set; }

    public bool RecoveryNeedsAttention { get; private set; }

    public IReadOnlyCollection<VolumeBackupReplica> Replicas => _replicas.AsReadOnly();

    public bool HasLocalCopy => ArtifactBucket is not null && ArtifactKey is not null;

    public bool HasRestorableArtifact => Status == VolumeBackupStatus.Succeeded && ArtifactKey is not null &&
                                         (ArtifactBucket is not null || _replicas.Any(replica => replica.IsAvailable));

    public bool IsActive => Status is VolumeBackupStatus.Queued or VolumeBackupStatus.Running;

    public IReadOnlyList<string> StoppedContainers => SplitContainers(StoppedContainerIds);

    public static VolumeBackupExecution Queue(TeamId teamId, ScheduledVolumeBackupId scheduleId, bool manual,
        DateTimeOffset now)
    {
        return new VolumeBackupExecution(VolumeBackupExecutionId.New(), teamId, scheduleId, manual, now);
    }

    public void Start(DateTimeOffset now)
    {
        if (Status != VolumeBackupStatus.Queued) throw new DomainException("Only queued backups can be started.");
        Status = VolumeBackupStatus.Running;
        StartedAt = now;
        Touch(now);
    }

    public void Skip(string reason, DateTimeOffset now)
    {
        if (Status != VolumeBackupStatus.Queued) throw new DomainException("Only queued backups can be skipped.");
        FailureReason = Truncate(reason, 1000);
        Status = VolumeBackupStatus.Skipped;
        FinishedAt = now;
        Touch(now);
    }

    /// <summary>Records containers before they are stopped, so recovery can start them even if the worker dies.</summary>
    public void RecordContainersToStop(IReadOnlyCollection<string> containerIds, DateTimeOffset now)
    {
        StoppedContainerIds = JoinContainers(containerIds);
        StopRecoveryPending = StoppedContainerIds is not null;
        Touch(now);
    }

    /// <summary>Containers were started again (by the run itself or by recovery).</summary>
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
        RecoveryLastAttemptAt = now;
        RecoveryError = Truncate(error, 1000);
        if (RecoveryAttempts >= MaxRecoveryAttempts) RecoveryNeedsAttention = true;
        Touch(now);
    }

    /// <summary>Operator retry after "needs attention": the recovery job tries again from a fresh budget.</summary>
    public void ResetRecovery(DateTimeOffset now)
    {
        if (!StopRecoveryPending) throw new DomainException("This backup has no pending recovery.");
        RecoveryAttempts = 0;
        RecoveryNeedsAttention = false;
        Touch(now);
    }

    public void RecordArtifact(string key, long sizeBytes, string checksumSha256, string? warning, DateTimeOffset now)
    {
        if (Status != VolumeBackupStatus.Running)
            throw new DomainException("Only running backups can record an artifact.");
        ArtifactKey = DomainValidation.Required(key, nameof(key), 512);
        if (sizeBytes < 0) throw new DomainException("Backup size cannot be negative.");
        SizeBytes = sizeBytes;
        ChecksumSha256 = DomainValidation.Required(checksumSha256, nameof(checksumSha256), 128);
        Warning = warning is null ? null : Truncate(warning, 1000);
        Touch(now);
    }

    public void RecordReplica(BackupDestinationId destinationId, string bucket, bool uploaded, string? message,
        DateTimeOffset now)
    {
        if (Status != VolumeBackupStatus.Running)
            throw new DomainException("Only running backups can record remote copies.");
        _replicas.RemoveAll(replica => replica.BackupDestinationId == destinationId);
        _replicas.Add(VolumeBackupReplica.Create(Id, destinationId, bucket, uploaded, message, now));
        Touch(now);
    }

    public void Complete(string? localBucket, DateTimeOffset now)
    {
        if (Status != VolumeBackupStatus.Running) throw new DomainException("Only running backups can succeed.");
        if (ArtifactKey is null) throw new DomainException("The backup artifact was not recorded.");
        ArtifactBucket = localBucket is null ? null : DomainValidation.Required(localBucket, nameof(localBucket), 160);
        if (!HasLocalCopy && !_replicas.Any(replica => replica.IsAvailable))
            throw new DomainException("The backup was not stored on any destination.");
        Status = VolumeBackupStatus.Succeeded;
        FinishedAt = now;
        Touch(now);
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        if (!IsActive) throw new DomainException($"Cannot fail a volume backup from terminal status {Status}.");
        FailureReason = Truncate(string.IsNullOrWhiteSpace(reason) ? "Backup failed." : reason.Trim(), 1000);
        Status = VolumeBackupStatus.Failed;
        FinishedAt = now;
        Touch(now);
    }

    public void MarkLocalCopyDeleted(DateTimeOffset now)
    {
        ArtifactBucket = null;
        MarkPrunedWhenNoCopiesRemain(now);
    }

    public void MarkReplicaDeleted(BackupDestinationId destinationId, DateTimeOffset now)
    {
        VolumeBackupReplica? replica = _replicas.SingleOrDefault(item => item.BackupDestinationId == destinationId);
        if (replica is null) return;
        replica.MarkDeleted(now);
        MarkPrunedWhenNoCopiesRemain(now);
    }

    public void Protect(DateTimeOffset now)
    {
        Protected = true;
        Touch(now);
    }

    private void MarkPrunedWhenNoCopiesRemain(DateTimeOffset now)
    {
        if (!HasLocalCopy && !_replicas.Any(replica => replica.IsAvailable) && Status == VolumeBackupStatus.Succeeded)
            Status = VolumeBackupStatus.Pruned;
        Touch(now);
    }

    internal static string? JoinContainers(IReadOnlyCollection<string> containerIds)
    {
        string[] ids = containerIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal).ToArray();
        foreach (var id in ids)
            if (id.Length > 128 || !id.All(char.IsAsciiLetterOrDigit))
                throw new DomainException("Container IDs must be alphanumeric.");
        if (ids.Length == 0) return null;
        var joined = string.Join(',', ids);
        if (joined.Length > MaxContainerIdsLength)
            throw new DomainException("Too many containers mount this target to stop them safely.");
        return joined;
    }

    internal static IReadOnlyList<string> SplitContainers(string? value)
    {
        return string.IsNullOrEmpty(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }

    internal static string Truncate(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}

/// <summary>One copy of a volume backup archive on a remote destination.</summary>
public sealed class VolumeBackupReplica
{
    private VolumeBackupReplica()
    {
    }

    public Guid Id { get; private set; }

    public VolumeBackupExecutionId VolumeBackupExecutionId { get; private set; }

    public BackupDestinationId? BackupDestinationId { get; private set; }

    public string Bucket { get; private set; } = string.Empty;

    public bool Uploaded { get; private set; }

    public string? Message { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsAvailable => Uploaded && DeletedAt is null && BackupDestinationId is not null;

    internal static VolumeBackupReplica Create(VolumeBackupExecutionId executionId,
        BackupDestinationId destinationId, string bucket, bool uploaded, string? message, DateTimeOffset now)
    {
        return new VolumeBackupReplica
        {
            Id = Guid.NewGuid(),
            VolumeBackupExecutionId = executionId,
            BackupDestinationId = destinationId,
            Bucket = DomainValidation.Required(bucket, nameof(bucket), 63),
            Uploaded = uploaded,
            Message = string.IsNullOrWhiteSpace(message)
                ? null
                : VolumeBackupExecution.Truncate(message, BackupArtifactReplica.MaxMessageLength),
            CreatedAt = now
        };
    }

    internal void MarkDeleted(DateTimeOffset now)
    {
        DeletedAt ??= now;
    }
}
