using Vessel.Domain.Common;

namespace Vessel.Domain.Operations;

/// <summary>
///     Steps of a control-plane upgrade (upstream <c>CoolifyUpgradeStatus</c> steps). Vessel prepares the upgrade
///     (backup, drain) and the Phase 14 <c>vessel self-update</c> CLI reports the swap, migration and health steps.
/// </summary>
public enum UpgradeRunStatus
{
    Pending = 0,
    BackingUp = 1,
    Draining = 2,
    ReadyForSwap = 3,
    Swapping = 4,
    Migrating = 5,
    Verifying = 6,
    Succeeded = 7,
    Failed = 8,
    RolledBack = 9,
    Aborted = 10
}

/// <summary>Durable status record of one upgrade attempt, polled by the UI and API (OPS-02).</summary>
public sealed class UpgradeRun : Entity<UpgradeRunId>
{
    public const int MaxMessageLength = 2000;

    /// <summary>A non-terminal run without a heartbeat for this long is treated as abandoned (upstream STALE_AFTER_MINUTES).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private UpgradeRun()
    {
    }

    private UpgradeRun(UpgradeRunId id, string fromVersion, string targetVersion, UpdateChannel channel,
        UserId? startedBy, string workerHost, DateTimeOffset now)
        : base(id, now)
    {
        FromVersion = DomainValidation.Required(fromVersion, nameof(FromVersion), 128);
        TargetVersion = DomainValidation.Required(targetVersion, nameof(TargetVersion), 128);
        Channel = channel;
        StartedByUserId = startedBy;
        WorkerHost = DomainValidation.Required(workerHost, nameof(WorkerHost), 128);
        Status = UpgradeRunStatus.Pending;
        HeartbeatAt = now;
        Message = "Upgrade requested.";
    }

    public string FromVersion { get; private set; } = string.Empty;

    public string TargetVersion { get; private set; } = string.Empty;

    public UpdateChannel Channel { get; private set; }

    public UpgradeRunStatus Status { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public UserId? StartedByUserId { get; private set; }

    public string WorkerHost { get; private set; } = string.Empty;

    public ControlPlaneBackupId? PreUpdateBackupId { get; private set; }

    public DateTimeOffset HeartbeatAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public bool IsTerminal => IsTerminalStatus(Status);

    public static bool IsTerminalStatus(UpgradeRunStatus status)
    {
        return status is UpgradeRunStatus.Succeeded or UpgradeRunStatus.Failed or UpgradeRunStatus.RolledBack
            or UpgradeRunStatus.Aborted;
    }

    public static UpgradeRun Start(string fromVersion, string targetVersion, UpdateChannel channel, UserId? startedBy,
        string workerHost, DateTimeOffset now)
    {
        return new UpgradeRun(UpgradeRunId.New(), fromVersion, targetVersion, channel, startedBy, workerHost, now);
    }

    public bool IsStale(DateTimeOffset now)
    {
        return !IsTerminal && now - HeartbeatAt > StaleAfter;
    }

    /// <summary>Moves to a later step (or a terminal state). Terminal runs never change again.</summary>
    public void Advance(UpgradeRunStatus status, string? message, DateTimeOffset now)
    {
        if (IsTerminal) throw new DomainException("The upgrade already finished.");
        if (status == UpgradeRunStatus.Pending) throw new DomainException("An upgrade cannot return to pending.");
        if (!IsTerminalStatus(status) && status < Status)
            throw new DomainException($"The upgrade cannot go back from {Status} to {status}.");
        Status = status;
        if (!string.IsNullOrWhiteSpace(message)) Message = Clip(message);
        HeartbeatAt = now;
        if (IsTerminalStatus(status)) FinishedAt = now;
        Touch(now);
    }

    public void Heartbeat(DateTimeOffset now)
    {
        if (IsTerminal) return;
        HeartbeatAt = now;
        Touch(now);
    }

    public void AttachBackup(ControlPlaneBackupId backupId, DateTimeOffset now)
    {
        PreUpdateBackupId = backupId;
        Touch(now);
    }

    private static string Clip(string message)
    {
        var text = message.Trim();
        return text.Length <= MaxMessageLength ? text : text[..MaxMessageLength];
    }
}
