using Vessel.Domain.Common;

namespace Vessel.Domain.Operations;

/// <summary>Why a control-plane database dump was taken; each trigger has its own folder and retention.</summary>
public enum ControlPlaneBackupTrigger
{
    Manual = 0,
    PreUpdate = 1,
    Scheduled = 2
}

public enum ControlPlaneBackupStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2
}

/// <summary>
///     One dump of Vessel's own PostgreSQL database (DATA-13 / OPS-02). The file lives in the owned control-plane backup
///     directory under <see cref="RelativePath" />, next to a <c>.sha256</c> checksum file.
/// </summary>
public sealed class ControlPlaneBackup : Entity<ControlPlaneBackupId>
{
    public const int MaxFailureReasonLength = 2000;

    private ControlPlaneBackup()
    {
    }

    private ControlPlaneBackup(ControlPlaneBackupId id, ControlPlaneBackupTrigger trigger, UserId? requestedBy,
        string workerHost, DateTimeOffset now)
        : base(id, now)
    {
        Trigger = trigger;
        RequestedByUserId = requestedBy;
        WorkerHost = DomainValidation.Required(workerHost, nameof(WorkerHost), 128);
        Status = ControlPlaneBackupStatus.Running;
        StartedAt = now;
    }

    public ControlPlaneBackupTrigger Trigger { get; private set; }

    public ControlPlaneBackupStatus Status { get; private set; }

    /// <summary>Path below the backup root (for example <c>pre-update/vessel-20261121T120000Z-1a2b3c4d.dump</c>).</summary>
    public string? RelativePath { get; private set; }

    public long SizeBytes { get; private set; }

    public string? ChecksumSha256 { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Redacted reason of a failed dump.</summary>
    public string? FailureReason { get; private set; }

    public UserId? RequestedByUserId { get; private set; }

    public string WorkerHost { get; private set; } = string.Empty;

    public static ControlPlaneBackup Start(ControlPlaneBackupTrigger trigger, UserId? requestedBy, string workerHost,
        DateTimeOffset now)
    {
        return new ControlPlaneBackup(ControlPlaneBackupId.New(), trigger, requestedBy, workerHost, now);
    }

    public void Succeed(string relativePath, long sizeBytes, string sha256, DateTimeOffset now)
    {
        if (Status != ControlPlaneBackupStatus.Running) throw new DomainException("The backup already finished.");
        if (sizeBytes < 0) throw new DomainException("Backup size cannot be negative.");
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigitLower))
            throw new DomainException("Backup checksum must be a lower-case SHA-256 hex digest.");
        RelativePath = DomainValidation.Required(relativePath, nameof(RelativePath), 512);
        SizeBytes = sizeBytes;
        ChecksumSha256 = sha256;
        Status = ControlPlaneBackupStatus.Succeeded;
        FinishedAt = now;
        Touch(now);
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        if (Status != ControlPlaneBackupStatus.Running) throw new DomainException("The backup already finished.");
        var text = string.IsNullOrWhiteSpace(reason) ? "The backup failed." : reason.Trim();
        FailureReason = text.Length <= MaxFailureReasonLength ? text : text[..MaxFailureReasonLength];
        Status = ControlPlaneBackupStatus.Failed;
        FinishedAt = now;
        Touch(now);
    }

    public static string FolderFor(ControlPlaneBackupTrigger trigger)
    {
        return trigger switch
        {
            ControlPlaneBackupTrigger.PreUpdate => "pre-update",
            ControlPlaneBackupTrigger.Scheduled => "scheduled",
            _ => "manual"
        };
    }
}
