using Vessel.Application.Jobs.Scheduling;
using Vessel.Application.Operations.ControlPlaneBackups;
using Vessel.Application.Operations.Updates;
using Vessel.Application.Settings;
using Vessel.Domain.Scheduling;

namespace Vessel.Application.Operations;

/// <summary>Fixed schedule identifiers of instance-wide schedules in the scheduled job ledger.</summary>
public static class InstanceScheduleIds
{
    public static readonly Guid ControlPlaneBackup = new("6c1f0d0e-7a1b-4c8e-9a51-0b5c2f9e3a01");

    public static readonly Guid UpdateCheck = new("6c1f0d0e-7a1b-4c8e-9a51-0b5c2f9e3a02");
}

/// <summary>Scheduled dumps of Vessel's own database (DATA-13) dispatched by the ledger (NTF-08), always in UTC.</summary>
public sealed class ControlPlaneBackupScheduleSource(ControlPlaneBackupService backups) : IScheduledJobSource
{
    public ScheduledJobKind Kind => ScheduledJobKind.ControlPlaneBackup;

    public IReadOnlyList<ScheduledJobDefinition> ListActiveSchedules()
    {
        InstanceBackupSettings settings = backups.LoadSettings();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Schedule)) return [];
        return
        [
            new ScheduledJobDefinition(Kind, InstanceScheduleIds.ControlPlaneBackup, null, "Vessel database",
                settings.Schedule, "UTC", MissedRunPolicy.RunOnce, 6 * 3600, DateTimeOffset.MinValue)
        ];
    }

    public async Task<ScheduledJobOutcome> RunAsync(ScheduledJobRun run, CancellationToken cancellationToken)
    {
        ControlPlaneBackupSummary? backup = await backups.RunScheduledAsync(cancellationToken);
        if (backup is null)
            return ScheduledJobOutcome.Skipped("Scheduled backups are disabled or another dump is running.");
        return backup.Status == Domain.Operations.ControlPlaneBackupStatus.Succeeded
            ? ScheduledJobOutcome.Succeeded(null, backup.Id)
            : ScheduledJobOutcome.Failed(backup.FailureReason ?? "The database dump failed.", backup.Id);
    }
}

/// <summary>The jittered daily update check (OPS-01), dispatched by the ledger only once a manifest URL is configured.</summary>
public sealed class UpdateCheckScheduleSource(SystemSettingsStore settings, UpdateCheckService checks)
    : IScheduledJobSource
{
    public ScheduledJobKind Kind => ScheduledJobKind.UpdateCheck;

    public IReadOnlyList<ScheduledJobDefinition> ListActiveSchedules()
    {
        UpdatePolicy policy = settings.Get(UpdatePolicyService.SettingKey, new UpdatePolicy());
        if (string.IsNullOrWhiteSpace(policy.ManifestUrl) || string.IsNullOrWhiteSpace(policy.CheckCron)) return [];
        return
        [
            new ScheduledJobDefinition(Kind, InstanceScheduleIds.UpdateCheck, null, "Update check", policy.CheckCron,
                "UTC", MissedRunPolicy.RunOnce, 300, DateTimeOffset.MinValue)
        ];
    }

    public async Task<ScheduledJobOutcome> RunAsync(ScheduledJobRun run, CancellationToken cancellationToken)
    {
        UpdateCheckResult result = await checks.RunScheduledCheckAsync(cancellationToken);
        return result.Status == UpdateCheckStatus.Failed
            ? ScheduledJobOutcome.Failed(result.Message ?? "The update check failed.")
            : ScheduledJobOutcome.Succeeded(result.Status.ToString());
    }
}
