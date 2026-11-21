namespace Vessel.Domain.Backups;

public enum BackupExecutionStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Pruned = 4,
    RestoreValidated = 5,
    RestoreSucceeded = 6,
    RestoreFailed = 7,

    /// <summary>
    ///     The run did not dump anything because another lifecycle, backup or restore operation held the database, or
    ///     the database was not running. Skipped runs are neither failures nor successes.
    /// </summary>
    Skipped = 8
}
