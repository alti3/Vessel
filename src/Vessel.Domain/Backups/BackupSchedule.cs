using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Backups;

public sealed class BackupSchedule : Entity<BackupScheduleId>
{
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
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        DatabaseResourceId = databaseResourceId;
        Name = name;
        CronExpression = ValidateCron(cronExpression);
        RetentionCount = ValidateRetention(retentionCount);
        StorageKind = storageKind;
    }

    public TeamId TeamId { get; private set; }

    public DatabaseResourceId DatabaseResourceId { get; private set; }

    public ResourceName Name { get; private set; }

    public string CronExpression { get; private set; } = string.Empty;

    public int RetentionCount { get; private set; }

    public BackupStorageKind StorageKind { get; private set; }

    public bool Enabled { get; private set; } = true;

    public DateTimeOffset? LastRunAt { get; private set; }

    public static BackupSchedule Create(
        TeamId teamId,
        DatabaseResourceId databaseResourceId,
        ResourceName name,
        string cronExpression,
        int retentionCount,
        BackupStorageKind storageKind,
        DateTimeOffset now)
    {
        return new BackupSchedule(
            BackupScheduleId.New(),
            teamId,
            databaseResourceId,
            name,
            cronExpression,
            retentionCount,
            storageKind,
            now);
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

    private static int ValidateRetention(int retentionCount)
    {
        return retentionCount is >= 1 and <= 500
            ? retentionCount
            : throw new DomainException("Backup retention count must be between 1 and 500.");
    }
}
