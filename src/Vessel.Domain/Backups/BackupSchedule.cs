using Vessel.Domain.Common;
using Vessel.Domain.Scheduling;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Backups;

public sealed class BackupSchedule : Entity<BackupScheduleId>
{
    /// <summary>Default per-run backup timeout (1 hour), matching upstream Coolify's database backup default.</summary>
    public const int DefaultTimeoutSeconds = 3600;

    public const int MinTimeoutSeconds = 60;

    public const int MaxTimeoutSeconds = 86400;

    public const int MaxMissingAlertDays = 365;

    public const int MaxDestinations = 10;

    /// <summary>After the first failure notification, further failures notify at most this often.</summary>
    public static readonly TimeSpan FailureNotificationInterval = TimeSpan.FromDays(1);

    private readonly List<BackupScheduleDestination> _destinations = [];

    private BackupSchedule()
    {
    }

    private BackupSchedule(
        BackupScheduleId id,
        TeamId teamId,
        DatabaseResourceId databaseResourceId,
        ResourceName name,
        string cronExpression,
        int retentionCount,
        BackupStorageKind storageKind,
        int timeoutSeconds,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        DatabaseResourceId = databaseResourceId;
        Name = name;
        CronExpression = ValidateCron(cronExpression);
        RetentionCount = ValidateRetention(retentionCount);
        StorageKind = storageKind;
        TimeoutSeconds = ValidateTimeout(timeoutSeconds);
    }

    public TeamId TeamId { get; private set; }

    public DatabaseResourceId DatabaseResourceId { get; private set; }

    public ResourceName Name { get; private set; }

    public string CronExpression { get; private set; } = string.Empty;

    /// <summary>Local retention: number of newest local copies to keep (1-500).</summary>
    public int RetentionCount { get; private set; }

    /// <summary>
    ///     Summary of where copies go: <see cref="BackupStorageKind.ObjectStorage" /> when at least one remote destination
    ///     is attached, otherwise <see cref="BackupStorageKind.Local" />.
    /// </summary>
    public BackupStorageKind StorageKind { get; private set; }

    public bool Enabled { get; private set; } = true;

    /// <summary>
    ///     Deadline for one whole backup run (dump, compression and upload). Each step only gets the time that remains.
    /// </summary>
    public int TimeoutSeconds { get; private set; } = DefaultTimeoutSeconds;

    public DateTimeOffset? LastRunAt { get; private set; }

    /// <summary>Persisted form of <see cref="Databases" />.</summary>
    public string DatabaseSelection { get; private set; } = "*";

    public BackupDatabaseSelection Databases => BackupDatabaseSelection.FromStorage(DatabaseSelection);

    public BackupDumpFormat DumpFormat { get; private set; } = BackupDumpFormat.Native;

    public int LocalRetentionDays { get; private set; }

    public long LocalRetentionMaxBytes { get; private set; }

    public int RemoteRetentionCount { get; private set; }

    public int RemoteRetentionDays { get; private set; }

    public long RemoteRetentionMaxBytes { get; private set; }

    public BackupRetentionPolicy LocalRetention =>
        new(RetentionCount, LocalRetentionDays, LocalRetentionMaxBytes);

    public BackupRetentionPolicy RemoteRetention =>
        new(RemoteRetentionCount, RemoteRetentionDays, RemoteRetentionMaxBytes);

    /// <summary>Keep copies only on remote destinations (upstream <c>disable_local_backup</c>).</summary>
    public bool DisableLocalCopy { get; private set; }

    public IReadOnlyCollection<BackupScheduleDestination> Destinations => _destinations.AsReadOnly();

    public IReadOnlyList<BackupDestinationId> DestinationIds =>
        _destinations.Select(destination => destination.BackupDestinationId).ToArray();

    /// <summary>Alert when no backup succeeded for this many days (0 = off).</summary>
    public int MissingAlertDays { get; private set; }

    public DateTimeOffset? MissingAlertSentAt { get; private set; }

    public DateTimeOffset? LastSucceededAt { get; private set; }

    /// <summary>Start of the current failure streak; <see langword="null" /> while the schedule is healthy.</summary>
    public DateTimeOffset? FailingSince { get; private set; }

    public DateTimeOffset? LastFailureNotifiedAt { get; private set; }

    /// <summary>IANA time zone the cron expression is evaluated in; null uses the instance default (NTF-08).</summary>
    public string? TimeZone { get; private set; }

    /// <summary>Catch-up policy after control-plane downtime; null uses the instance default (NTF-08).</summary>
    public MissedRunPolicy? MissedRunPolicy { get; private set; }

    public static BackupSchedule Create(
        TeamId teamId,
        DatabaseResourceId databaseResourceId,
        ResourceName name,
        string cronExpression,
        int retentionCount,
        BackupStorageKind storageKind,
        DateTimeOffset now,
        int timeoutSeconds = DefaultTimeoutSeconds)
    {
        return new BackupSchedule(
            BackupScheduleId.New(),
            teamId,
            databaseResourceId,
            name,
            cronExpression,
            retentionCount,
            storageKind,
            timeoutSeconds,
            now);
    }

    public void ChangeTimeout(int timeoutSeconds, DateTimeOffset now)
    {
        TimeoutSeconds = ValidateTimeout(timeoutSeconds);
        Touch(now);
    }

    public void Update(ResourceName name, string cronExpression, int retentionCount, BackupStorageKind storageKind,
        bool enabled, DateTimeOffset now)
    {
        Name = name;
        CronExpression = ValidateCron(cronExpression);
        RetentionCount = ValidateRetention(retentionCount);
        StorageKind = storageKind;
        Enabled = enabled;
        Touch(now);
    }

    /// <summary>Applies the full schedule settings (create and edit). Destinations must belong to the same team.</summary>
    public void Configure(
        ResourceName name,
        string cronExpression,
        BackupDatabaseSelection databases,
        BackupDumpFormat dumpFormat,
        BackupRetentionPolicy localRetention,
        BackupRetentionPolicy remoteRetention,
        IReadOnlyCollection<BackupDestinationId> destinationIds,
        bool disableLocalCopy,
        int timeoutSeconds,
        int missingAlertDays,
        DateTimeOffset now)
    {
        BackupDestinationId[] distinct = destinationIds.Distinct().ToArray();
        if (distinct.Length > MaxDestinations)
            throw new DomainException($"A backup schedule can use at most {MaxDestinations} destinations.");
        if (disableLocalCopy && distinct.Length == 0)
            throw new DomainException("Keep a local copy or select at least one remote destination.");
        if (missingAlertDays is < 0 or > MaxMissingAlertDays)
            throw new DomainException($"Missing-backup alert days must be between 0 and {MaxMissingAlertDays}.");
        Name = name;
        CronExpression = ValidateCron(cronExpression);
        DatabaseSelection = databases.ToStorage();
        DumpFormat = dumpFormat;
        RetentionCount = ValidateRetention(localRetention.Count);
        LocalRetentionDays = localRetention.Days;
        LocalRetentionMaxBytes = localRetention.MaxBytes;
        RemoteRetentionCount = remoteRetention.Count;
        RemoteRetentionDays = remoteRetention.Days;
        RemoteRetentionMaxBytes = remoteRetention.MaxBytes;
        _destinations.RemoveAll(destination => !distinct.Contains(destination.BackupDestinationId));
        foreach (BackupDestinationId destinationId in distinct)
            if (_destinations.All(destination => destination.BackupDestinationId != destinationId))
                _destinations.Add(new BackupScheduleDestination(Id, destinationId));
        DisableLocalCopy = disableLocalCopy;
        StorageKind = distinct.Length > 0 ? BackupStorageKind.ObjectStorage : BackupStorageKind.Local;
        TimeoutSeconds = ValidateTimeout(timeoutSeconds);
        if (MissingAlertDays != missingAlertDays) MissingAlertSentAt = null;
        MissingAlertDays = missingAlertDays;
        Touch(now);
    }

    /// <summary>
    ///     Detaches a deleted destination. When it was the only copy location the local copy is re-enabled so the
    ///     schedule keeps producing restorable backups.
    /// </summary>
    public void DetachDestination(BackupDestinationId destinationId, DateTimeOffset now)
    {
        if (_destinations.RemoveAll(destination => destination.BackupDestinationId == destinationId) == 0) return;
        if (_destinations.Count == 0)
        {
            DisableLocalCopy = false;
            StorageKind = BackupStorageKind.Local;
        }

        Touch(now);
    }

    /// <summary>Sets the time zone and the catch-up policy of the schedule (null values use the instance defaults).</summary>
    public void ConfigureScheduling(string? timeZone, MissedRunPolicy? missedRunPolicy, DateTimeOffset now)
    {
        var zone = string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim();
        if (zone is not null) CronSchedule.ResolveTimeZone(zone);
        TimeZone = zone;
        MissedRunPolicy = missedRunPolicy;
        Touch(now);
    }

    public void Enable(DateTimeOffset now)
    {
        if (Enabled) return;
        Enabled = true;
        // A re-enabled schedule starts a fresh idle period for missing-backup alerts.
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

    /// <summary>Records a successful run.</summary>
    /// <returns><see langword="true" /> when this success ends a failure streak (send one recovery notice).</returns>
    public bool RecordSuccess(DateTimeOffset now)
    {
        var recovered = FailingSince is not null;
        LastSucceededAt = now;
        FailingSince = null;
        LastFailureNotifiedAt = null;
        Touch(now);
        return recovered;
    }

    /// <summary>Records that a run stored a restorable copy (it may still count as a failure, see below).</summary>
    public void RecordStored(DateTimeOffset now)
    {
        LastSucceededAt = now;
        Touch(now);
    }

    /// <summary>
    ///     Records a failed run. The first failure of a streak notifies; later failures notify at most once per
    ///     <see cref="FailureNotificationInterval" /> (upstream v4.7.0 throttling).
    /// </summary>
    /// <returns><see langword="true" /> when a failure notification should be sent now.</returns>
    public bool RecordFailure(DateTimeOffset now)
    {
        FailingSince ??= now;
        var notify = LastFailureNotifiedAt is null || now - LastFailureNotifiedAt.Value >= FailureNotificationInterval;
        if (notify) LastFailureNotifiedAt = now;
        Touch(now);
        return notify;
    }

    /// <summary>
    ///     True when missing-backup alerts are on, the schedule is enabled, no backup succeeded for
    ///     <see cref="MissingAlertDays" /> days (counting from creation when none ever succeeded) and no alert was sent
    ///     for the current idle period.
    /// </summary>
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
        if (value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 5 && !value.StartsWith('@'))
            throw new DomainException("Backup schedule cron expression must contain at least five fields.");
        if (!CronSchedule.TryParse(value, out _, out var error))
            throw new DomainException($"Backup schedule cron expression is invalid: {error}");
        return value;
    }

    private static int ValidateTimeout(int timeoutSeconds)
    {
        return timeoutSeconds is >= MinTimeoutSeconds and <= MaxTimeoutSeconds
            ? timeoutSeconds
            : throw new DomainException(
                $"Backup timeout must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds} seconds.");
    }

    private static int ValidateRetention(int retentionCount)
    {
        return retentionCount is >= 1 and <= BackupRetentionPolicy.MaxCount
            ? retentionCount
            : throw new DomainException("Backup retention count must be between 1 and 500.");
    }
}

/// <summary>Join row: one remote destination selected by a backup schedule.</summary>
public sealed class BackupScheduleDestination
{
    private BackupScheduleDestination()
    {
    }

    internal BackupScheduleDestination(BackupScheduleId backupScheduleId, BackupDestinationId backupDestinationId)
    {
        BackupScheduleId = backupScheduleId;
        BackupDestinationId = backupDestinationId;
    }

    public BackupScheduleId BackupScheduleId { get; private set; }

    public BackupDestinationId BackupDestinationId { get; private set; }
}
