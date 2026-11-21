using Vessel.Domain.Common;
using Vessel.Domain.Storage;

namespace Vessel.Domain.Backups;

/// <summary>What a volume backup archives: a named persistent volume or a directory mount of the resource directory.</summary>
public enum VolumeBackupTargetKind
{
    PersistentVolume = 0,
    DirectoryMount = 1
}

/// <summary>
///     Scheduled backup of one persistent volume or directory mount (upstream <c>scheduled_volume_backups</c>). There
///     is one schedule per target. Runs archive the target with tar+gzip in a short-lived helper container on the
///     resource's server and store the archive locally and/or on team backup destinations, with independent retention.
/// </summary>
public sealed class ScheduledVolumeBackup : Entity<ScheduledVolumeBackupId>
{
    /// <summary>Upstream <c>ScheduledVolumeBackup::DEFAULT_TIMEOUT</c> (10 hours).</summary>
    public const int DefaultTimeoutSeconds = 36000;

    public const int MinTimeoutSeconds = 60;

    public const int MaxTimeoutSeconds = 86400;

    public const int DefaultRetentionCount = 7;

    private readonly List<ScheduledVolumeBackupDestination> _destinations = [];

    private ScheduledVolumeBackup()
    {
    }

    private ScheduledVolumeBackup(ScheduledVolumeBackupId id, TeamId teamId, StorageOwnerKind ownerKind, Guid ownerId,
        VolumeBackupTargetKind targetKind, Guid targetId, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        OwnerKind = ownerKind;
        OwnerId = ownerId;
        TargetKind = targetKind;
        TargetId = targetId;
    }

    public TeamId TeamId { get; private set; }

    public StorageOwnerKind OwnerKind { get; private set; }

    public Guid OwnerId { get; private set; }

    public VolumeBackupTargetKind TargetKind { get; private set; }

    /// <summary><see cref="PersistentVolumeId" /> or <see cref="FileMountId" /> value, per <see cref="TargetKind" />.</summary>
    public Guid TargetId { get; private set; }

    public string CronExpression { get; private set; } = "0 0 * * *";

    public bool Enabled { get; private set; } = true;

    /// <summary>Stop the containers that mount the target while it is archived, then start them again.</summary>
    public bool StopDuringBackup { get; private set; }

    public bool DisableLocalCopy { get; private set; }

    public int TimeoutSeconds { get; private set; } = DefaultTimeoutSeconds;

    public int LocalRetentionCount { get; private set; } = DefaultRetentionCount;

    public int LocalRetentionDays { get; private set; }

    public long LocalRetentionMaxBytes { get; private set; }

    public int RemoteRetentionCount { get; private set; } = DefaultRetentionCount;

    public int RemoteRetentionDays { get; private set; }

    public long RemoteRetentionMaxBytes { get; private set; }

    public BackupRetentionPolicy LocalRetention => new(LocalRetentionCount, LocalRetentionDays, LocalRetentionMaxBytes);

    public BackupRetentionPolicy RemoteRetention =>
        new(RemoteRetentionCount, RemoteRetentionDays, RemoteRetentionMaxBytes);

    public IReadOnlyCollection<ScheduledVolumeBackupDestination> Destinations => _destinations.AsReadOnly();

    public IReadOnlyList<BackupDestinationId> DestinationIds =>
        _destinations.Select(destination => destination.BackupDestinationId).ToArray();

    /// <summary>Alert when no backup succeeded for this many days (0 = off); upstream <c>missing_backup_notification_days</c>.</summary>
    public int MissingAlertDays { get; private set; }

    public DateTimeOffset? MissingAlertSentAt { get; private set; }

    public DateTimeOffset? LastRunAt { get; private set; }

    public DateTimeOffset? LastSucceededAt { get; private set; }

    public DateTimeOffset? FailingSince { get; private set; }

    public DateTimeOffset? LastFailureNotifiedAt { get; private set; }

    public static ScheduledVolumeBackup Create(TeamId teamId, StorageOwnerKind ownerKind, Guid ownerId,
        VolumeBackupTargetKind targetKind, Guid targetId, DateTimeOffset now)
    {
        if (ownerId == Guid.Empty || targetId == Guid.Empty)
            throw new DomainException("Volume backup target is required.");
        return new ScheduledVolumeBackup(ScheduledVolumeBackupId.New(), teamId, ownerKind, ownerId, targetKind,
            targetId, now);
    }

    public void Configure(
        string cronExpression,
        bool stopDuringBackup,
        BackupRetentionPolicy localRetention,
        BackupRetentionPolicy remoteRetention,
        IReadOnlyCollection<BackupDestinationId> destinationIds,
        bool disableLocalCopy,
        int timeoutSeconds,
        int missingAlertDays,
        DateTimeOffset now)
    {
        BackupDestinationId[] distinct = destinationIds.Distinct().ToArray();
        if (distinct.Length > BackupSchedule.MaxDestinations)
            throw new DomainException($"A backup schedule can use at most {BackupSchedule.MaxDestinations} destinations.");
        if (disableLocalCopy && distinct.Length == 0)
            throw new DomainException("Keep a local copy or select at least one remote destination.");
        if (missingAlertDays is < 0 or > BackupSchedule.MaxMissingAlertDays)
            throw new DomainException(
                $"Missing-backup alert days must be between 0 and {BackupSchedule.MaxMissingAlertDays}.");
        if (timeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
            throw new DomainException(
                $"Backup timeout must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds} seconds.");
        if (localRetention.Count < 1)
            throw new DomainException("Local retention must keep at least one backup.");
        CronExpression = ValidateCron(cronExpression);
        StopDuringBackup = stopDuringBackup;
        LocalRetentionCount = localRetention.Count;
        LocalRetentionDays = localRetention.Days;
        LocalRetentionMaxBytes = localRetention.MaxBytes;
        RemoteRetentionCount = remoteRetention.Count;
        RemoteRetentionDays = remoteRetention.Days;
        RemoteRetentionMaxBytes = remoteRetention.MaxBytes;
        _destinations.RemoveAll(destination => !distinct.Contains(destination.BackupDestinationId));
        foreach (BackupDestinationId destinationId in distinct)
            if (_destinations.All(destination => destination.BackupDestinationId != destinationId))
                _destinations.Add(new ScheduledVolumeBackupDestination(Id, destinationId));
        DisableLocalCopy = disableLocalCopy;
        TimeoutSeconds = timeoutSeconds;
        if (MissingAlertDays != missingAlertDays) MissingAlertSentAt = null;
        MissingAlertDays = missingAlertDays;
        Touch(now);
    }

    /// <summary>Detaches a deleted destination; the local copy is re-enabled when it was the only location.</summary>
    public void DetachDestination(BackupDestinationId destinationId, DateTimeOffset now)
    {
        if (_destinations.RemoveAll(destination => destination.BackupDestinationId == destinationId) == 0) return;
        if (_destinations.Count == 0) DisableLocalCopy = false;
        Touch(now);
    }

    public void Enable(DateTimeOffset now)
    {
        if (Enabled) return;
        Enabled = true;
        MissingAlertSentAt = null;
        Touch(now);
    }

    public void Disable(DateTimeOffset now)
    {
        if (!Enabled) return;
        Enabled = false;
        Touch(now);
    }

    public void RecordRun(DateTimeOffset now)
    {
        LastRunAt = now;
        Touch(now);
    }

    /// <returns><see langword="true" /> when this success ends a failure streak.</returns>
    public bool RecordSuccess(DateTimeOffset now)
    {
        var recovered = FailingSince is not null;
        LastSucceededAt = now;
        FailingSince = null;
        LastFailureNotifiedAt = null;
        Touch(now);
        return recovered;
    }

    public void RecordStored(DateTimeOffset now)
    {
        LastSucceededAt = now;
        Touch(now);
    }

    /// <returns><see langword="true" /> when a failure notification should be sent now (first, then at most daily).</returns>
    public bool RecordFailure(DateTimeOffset now)
    {
        FailingSince ??= now;
        var notify = LastFailureNotifiedAt is null ||
                     now - LastFailureNotifiedAt.Value >= BackupSchedule.FailureNotificationInterval;
        if (notify) LastFailureNotifiedAt = now;
        Touch(now);
        return notify;
    }

    public bool IsBackupMissing(DateTimeOffset now)
    {
        if (!Enabled || MissingAlertDays <= 0) return false;
        DateTimeOffset since = LastSucceededAt ?? CreatedAt;
        if (now - since < TimeSpan.FromDays(MissingAlertDays)) return false;
        return MissingAlertSentAt is null || MissingAlertSentAt.Value < since;
    }

    public void RecordMissingAlertSent(DateTimeOffset now)
    {
        MissingAlertSentAt = now;
        Touch(now);
    }

    private static string ValidateCron(string cronExpression)
    {
        var value = DomainValidation.Required(cronExpression, nameof(CronExpression), 120);
        if (value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 5)
            throw new DomainException("Backup schedule cron expression must contain at least five fields.");
        return value;
    }
}

/// <summary>Join row: one remote destination selected by a volume backup schedule.</summary>
public sealed class ScheduledVolumeBackupDestination
{
    private ScheduledVolumeBackupDestination()
    {
    }

    internal ScheduledVolumeBackupDestination(ScheduledVolumeBackupId scheduleId, BackupDestinationId destinationId)
    {
        ScheduledVolumeBackupId = scheduleId;
        BackupDestinationId = destinationId;
    }

    public ScheduledVolumeBackupId ScheduledVolumeBackupId { get; private set; }

    public BackupDestinationId BackupDestinationId { get; private set; }
}
