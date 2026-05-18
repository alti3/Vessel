namespace Vessel.Domain.Databases;

public sealed class BackupPolicy
{
    private BackupPolicy()
    {
    }

    internal BackupPolicy(DatabaseResourceId databaseResourceId, string cronExpression, int retentionCount)
    {
        DatabaseResourceId = databaseResourceId;
        CronExpression = string.IsNullOrWhiteSpace(cronExpression)
            ? throw new ArgumentException("Cron expression is required.", nameof(cronExpression))
            : cronExpression.Trim();
        RetentionCount = retentionCount > 0
            ? retentionCount
            : throw new ArgumentOutOfRangeException(nameof(retentionCount));
    }

    public DatabaseResourceId DatabaseResourceId { get; private set; }

    public string CronExpression { get; private set; } = string.Empty;

    public int RetentionCount { get; private set; }
}
