using Vessel.Application.Jobs.Scheduling;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Scheduling;

namespace Vessel.Application.Backups;

/// <summary>
///     Database backup schedules in the scheduled job ledger (NTF-08). Each occurrence creates its executions exactly
///     once: the ledger run is claimed before <see cref="DatabaseBackupService.RunScheduledBackupAsync" /> runs and is
///     never retried, which fixes the duplicate executions a Hangfire retry used to create.
/// </summary>
public sealed class DatabaseBackupScheduleSource(IVesselDbContext dbContext, DatabaseBackupService backups)
    : IScheduledJobSource
{
    public ScheduledJobKind Kind => ScheduledJobKind.DatabaseBackup;

    public IReadOnlyList<ScheduledJobDefinition> ListActiveSchedules()
    {
        return dbContext.BackupSchedules
            .Where(schedule => schedule.Enabled)
            .AsEnumerable()
            .Select(schedule => new ScheduledJobDefinition(Kind, schedule.Id.Value, schedule.TeamId,
                schedule.Name.Value, schedule.CronExpression, schedule.TimeZone, schedule.MissedRunPolicy,
                // A selection of several databases runs one dump after the other.
                schedule.TimeoutSeconds * 2, schedule.CreatedAt))
            .ToArray();
    }

    public async Task<ScheduledJobOutcome> RunAsync(ScheduledJobRun run, CancellationToken cancellationToken)
    {
        IReadOnlyList<BackupExecutionSummary> executions =
            await backups.RunScheduledBackupAsync(new BackupScheduleId(run.ScheduleId), cancellationToken);
        if (executions.Count == 0) return ScheduledJobOutcome.Skipped("The schedule is disabled or was deleted.");
        Guid first = executions[0].Id;
        var failed = executions.Count(execution => execution.Status == BackupExecutionStatus.Failed);
        if (failed > 0)
            return ScheduledJobOutcome.Failed(
                failed == executions.Count
                    ? "The backup failed; see the backup execution for details."
                    : $"{failed} of {executions.Count} database backups failed; see the backup executions.", first);
        if (executions.All(execution => execution.Status == BackupExecutionStatus.Skipped))
            return ScheduledJobOutcome.Skipped("The backup was skipped; see the backup execution.", first);
        return ScheduledJobOutcome.Succeeded(
            executions.Count == 1 ? null : $"{executions.Count} database backups completed.", first);
    }
}
