using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Backups;

public sealed class BackupSchedule : Entity<BackupScheduleId>
{
    /// <summary>Default per-run backup timeout (1 hour), matching upstream Coolify's database backup default.</summary>
    public const int DefaultTimeoutSeconds = 3600;

    public const int MinTimeoutSeconds = 60;

    public const int MaxTimeoutSeconds = 86400;

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

    public int RetentionCount { get; private set; }

    public BackupStorageKind StorageKind { get; private set; }

    public bool Enabled { get; private set; } = true;

    /// <summary>
    /// Deadline for one whole backup run (dump, compression and upload). Each step only gets the time that remains.
    /// </summary>
    public int TimeoutSeconds { get; private set; } = DefaultTimeoutSeconds;

    public DateTimeOffset? LastRunAt { get; private set; }

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

    public void RecordRun(DateTimeOffset now)
    {
        LastRunAt = now;
        Touch(now);
    }

    private static string ValidateCron(string cronExpression)
    {
        var value = DomainValidation.Required(cronExpression, nameof(CronExpression), 120);
        if (value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 5)
            throw new DomainException("Backup schedule cron expression must contain at least five fields.");
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
        return retentionCount is >= 1 and <= 500
            ? retentionCount
            : throw new DomainException("Backup retention count must be between 1 and 500.");
    }
}
