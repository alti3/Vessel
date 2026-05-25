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
    RestoreFailed = 7
}
