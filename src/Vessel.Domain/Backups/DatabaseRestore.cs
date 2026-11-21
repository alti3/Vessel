using Vessel.Domain.Common;

namespace Vessel.Domain.Backups;

public enum DatabaseRestoreStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}

/// <summary>
/// One queued restore (or dry-run validation) of a backup artifact into a target database. Restores run as
/// background jobs; this record carries their state transitions, options and an incrementally persisted log.
/// An import (DATA-10) is a restore whose artifact comes from an external source (<see cref="ImportSource" />)
/// instead of a Vessel backup execution; it runs through the same verification, safety scanning and restore steps.
/// </summary>
public sealed class DatabaseRestore : Entity<DatabaseRestoreId>
{
    public const int MaxLogLength = 64 * 1024;

    public const int MaxFailureReasonLength = 1000;

    private const string TruncatedLogMarker = "[earlier log lines truncated]\n";

    private DatabaseRestore()
    {
    }

    public const int MaxImportReferenceLength = 1024;

    private DatabaseRestore(
        DatabaseRestoreId id,
        TeamId teamId,
        BackupExecutionId? backupExecutionId,
        DatabaseResourceId targetDatabaseResourceId,
        UserId? requestedByUserId,
        bool dryRun,
        bool replaceExisting,
        bool keepOwners,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        BackupExecutionId = backupExecutionId;
        TargetDatabaseResourceId = targetDatabaseResourceId;
        RequestedByUserId = requestedByUserId;
        DryRun = dryRun;
        ReplaceExisting = replaceExisting;
        KeepOwners = keepOwners;
    }

    public TeamId TeamId { get; private set; }

    /// <summary>The restored backup; <see langword="null" /> for imports.</summary>
    public BackupExecutionId? BackupExecutionId { get; private set; }

    public DatabaseImportSourceKind ImportSource { get; private set; } = DatabaseImportSourceKind.None;

    /// <summary>Upload ID, validated server path, or destination object key, per <see cref="ImportSource" />.</summary>
    public string? ImportReference { get; private set; }

    public BackupDestinationId? ImportDestinationId { get; private set; }

    /// <summary>Logical database an import restores into (PostgreSQL); <see langword="null" /> for the default.</summary>
    public string? ImportDatabaseName { get; private set; }

    public bool IsImport => ImportSource != DatabaseImportSourceKind.None;

    public DatabaseResourceId TargetDatabaseResourceId { get; private set; }

    public UserId? RequestedByUserId { get; private set; }

    public DatabaseRestoreStatus Status { get; private set; } = DatabaseRestoreStatus.Queued;

    public bool DryRun { get; private set; }

    /// <summary>Drop and recreate objects that already exist in the target (PostgreSQL archives only).</summary>
    public bool ReplaceExisting { get; private set; }

    /// <summary>Restore object owners and privileges from the archive (PostgreSQL archives only).</summary>
    public bool KeepOwners { get; private set; }

    public string? DetectedFormat { get; private set; }

    public string Log { get; private set; } = string.Empty;

    public string? FailureReason { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public bool IsActive => Status is DatabaseRestoreStatus.Queued or DatabaseRestoreStatus.Running;

    public static DatabaseRestore Queue(
        TeamId teamId,
        BackupExecutionId backupExecutionId,
        DatabaseResourceId targetDatabaseResourceId,
        UserId? requestedByUserId,
        bool dryRun,
        bool replaceExisting,
        bool keepOwners,
        DateTimeOffset now)
    {
        return new DatabaseRestore(DatabaseRestoreId.New(), teamId, backupExecutionId, targetDatabaseResourceId,
            requestedByUserId, dryRun, replaceExisting, keepOwners, now);
    }

    /// <summary>Queues an import of an external dump into <paramref name="targetDatabaseResourceId" />.</summary>
    public static DatabaseRestore QueueImport(
        TeamId teamId,
        DatabaseResourceId targetDatabaseResourceId,
        UserId requestedByUserId,
        DatabaseImportSourceKind source,
        string reference,
        BackupDestinationId? destinationId,
        string? databaseName,
        bool dryRun,
        bool replaceExisting,
        bool keepOwners,
        DateTimeOffset now)
    {
        if (source == DatabaseImportSourceKind.None) throw new DomainException("Import source is required.");
        if ((source == DatabaseImportSourceKind.Destination) != destinationId.HasValue)
            throw new DomainException("A destination import needs a backup destination.");
        var restore = new DatabaseRestore(DatabaseRestoreId.New(), teamId, null, targetDatabaseResourceId,
            requestedByUserId, dryRun, replaceExisting, keepOwners, now)
        {
            ImportSource = source,
            ImportReference = DomainValidation.Required(reference, "Import source", MaxImportReferenceLength),
            ImportDestinationId = destinationId,
            ImportDatabaseName = databaseName is null ? null : BackupDatabaseSelection.ValidateName(databaseName)
        };
        return restore;
    }

    public void Start(DateTimeOffset now)
    {
        if (Status != DatabaseRestoreStatus.Queued)
            throw new DomainException("Only queued restores can be started.");
        Status = DatabaseRestoreStatus.Running;
        StartedAt = now;
        Touch(now);
    }

    public void RecordDetectedFormat(string format, DateTimeOffset now)
    {
        DetectedFormat = DomainValidation.Required(format, nameof(format), 64);
        Touch(now);
    }

    /// <summary>
    /// Appends one line to the restore log. The log keeps the most recent <see cref="MaxLogLength" /> characters,
    /// since the final lines (the failing step) matter most. Callers must redact secrets before appending.
    /// </summary>
    public void AppendLog(string line, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(line)) return;
        var entry = $"[{now:O}] {line.TrimEnd()}\n";
        var combined = Log + entry;
        if (combined.Length > MaxLogLength)
            combined = TruncatedLogMarker + combined[^(MaxLogLength - TruncatedLogMarker.Length)..];
        Log = combined;
        Touch(now);
    }

    public void Succeed(DateTimeOffset now)
    {
        if (Status != DatabaseRestoreStatus.Running)
            throw new DomainException("Only running restores can succeed.");
        Status = DatabaseRestoreStatus.Succeeded;
        FinishedAt = now;
        Touch(now);
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        if (!IsActive)
            throw new DomainException($"Cannot fail a restore from terminal status {Status}.");
        var normalized = string.IsNullOrWhiteSpace(reason) ? "Restore failed." : reason.Trim();
        FailureReason = normalized.Length > MaxFailureReasonLength
            ? normalized[..MaxFailureReasonLength]
            : normalized;
        Status = DatabaseRestoreStatus.Failed;
        FinishedAt = now;
        Touch(now);
    }
}
