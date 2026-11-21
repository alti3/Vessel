using Vessel.Domain.Backups;
using Vessel.Domain.Scheduling;

namespace Vessel.Application.Backups;

/// <summary>
///     Retention for one copy location; every limit is optional (0 = unlimited). <see cref="MaxStorageGigabytes" /> is
///     converted to bytes.
/// </summary>
public sealed record BackupRetentionRequest(int Count = 0, int Days = 0, decimal MaxStorageGigabytes = 0);

/// <summary>
///     Create-schedule request (also the full replacement settings for an edit).
///     <see cref="Databases" /> is <c>null</c> or empty with <see cref="DumpAll" /> true for all databases, a list of
///     names for a named selection, and an empty list with <see cref="DumpAll" /> false for the engine default database.
/// </summary>
public sealed record CreateBackupScheduleRequest(
    Guid DatabaseId,
    string Name,
    string CronExpression,
    int RetentionCount = 7,
    int? TimeoutSeconds = null,
    bool DumpAll = true,
    IReadOnlyList<string>? Databases = null,
    BackupDumpFormat DumpFormat = BackupDumpFormat.Native,
    BackupRetentionRequest? LocalRetention = null,
    BackupRetentionRequest? RemoteRetention = null,
    IReadOnlyList<Guid>? DestinationIds = null,
    bool DisableLocalCopy = false,
    int MissingAlertDays = 0,
    bool Enabled = true,
    string? TimeZone = null,
    MissedRunPolicy? MissedRunPolicy = null);

/// <summary>Partial update of a schedule; <c>null</c> fields keep their current value.</summary>
public sealed record UpdateBackupScheduleRequest(
    string? Name = null,
    string? CronExpression = null,
    bool? Enabled = null,
    int? TimeoutSeconds = null,
    bool? DumpAll = null,
    IReadOnlyList<string>? Databases = null,
    BackupDumpFormat? DumpFormat = null,
    BackupRetentionRequest? LocalRetention = null,
    BackupRetentionRequest? RemoteRetention = null,
    IReadOnlyList<Guid>? DestinationIds = null,
    bool? DisableLocalCopy = null,
    int? MissingAlertDays = null,
    string? TimeZone = null,
    MissedRunPolicy? MissedRunPolicy = null,
    bool ClearTimeZone = false,
    bool ClearMissedRunPolicy = false);

public sealed record BackupRetentionSummary(int Count, int Days, long MaxBytes);

public sealed record BackupScheduleSummary(
    Guid Id,
    Guid DatabaseId,
    string Name,
    string CronExpression,
    int RetentionCount,
    BackupStorageKind StorageKind,
    bool Enabled,
    DateTimeOffset? LastRunAt,
    int TimeoutSeconds,
    BackupDatabaseSelectionMode DatabaseSelection,
    IReadOnlyList<string> Databases,
    BackupDumpFormat DumpFormat,
    BackupRetentionSummary LocalRetention,
    BackupRetentionSummary RemoteRetention,
    IReadOnlyList<Guid> DestinationIds,
    bool DisableLocalCopy,
    int MissingAlertDays,
    DateTimeOffset? LastSucceededAt,
    DateTimeOffset? FailingSince,
    string? TimeZone = null,
    MissedRunPolicy? MissedRunPolicy = null);

public sealed record BackupReplicaSummary(
    Guid? DestinationId,
    string Bucket,
    bool Uploaded,
    bool Deleted,
    string? Message,
    DateTimeOffset CreatedAt);

/// <summary>
///     One backup run. <see cref="FailureReason" /> and replica messages can contain tool output, so they are only
///     returned to callers with sensitive read access (<c>secrets.read</c>); <see cref="MessageHidden" /> tells others
///     that a message exists.
/// </summary>
public sealed record BackupExecutionSummary(
    Guid Id,
    Guid DatabaseId,
    Guid? ScheduleId,
    BackupExecutionStatus Status,
    BackupStorageKind StorageKind,
    string? ArtifactBucket,
    string? ArtifactKey,
    long? SizeBytes,
    string? ChecksumSha256,
    bool Protected,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    string? FailureReason,
    DateTimeOffset? LastRestoreFailedAt,
    string? LastRestoreFailureReason,
    string? DatabaseName = null,
    string? ArtifactFormat = null,
    bool HasLocalCopy = false,
    IReadOnlyList<BackupReplicaSummary>? Replicas = null,
    bool MessageHidden = false);

public sealed record PagedBackupExecutions(
    IReadOnlyList<BackupExecutionSummary> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>Manual backup request; <see cref="DatabaseName" /> null dumps the engine default database.</summary>
public sealed record QueueBackupRequest(string? DatabaseName = null);

/// <summary>A streamed backup artifact. Dispose to release the underlying store connection.</summary>
public sealed class BackupArtifactDownload(Stream content, string fileName, string contentType, long? length)
    : IAsyncDisposable
{
    public Stream Content { get; } = content;

    public string FileName { get; } = fileName;

    public string ContentType { get; } = contentType;

    public long? Length { get; } = length;

    public ValueTask DisposeAsync()
    {
        return Content.DisposeAsync();
    }
}

public sealed record CreateBackupDestinationRequest(
    string Name,
    string Endpoint,
    string Bucket,
    string AccessKeyId,
    string SecretAccessKey,
    string? Region = null,
    bool UsePathStyle = true,
    bool SkipTlsVerification = false,
    string? Description = null);

/// <summary>Partial update; supply both keys to replace credentials.</summary>
public sealed record UpdateBackupDestinationRequest(
    string? Name = null,
    string? Endpoint = null,
    string? Bucket = null,
    string? Region = null,
    bool? UsePathStyle = null,
    bool? SkipTlsVerification = null,
    string? AccessKeyId = null,
    string? SecretAccessKey = null,
    string? Description = null);

public sealed record BackupDestinationSummary(
    Guid Id,
    string Name,
    string? Description,
    BackupDestinationKind Kind,
    string Endpoint,
    string Region,
    string Bucket,
    bool UsePathStyle,
    bool SkipTlsVerification,
    bool IsUsable,
    DateTimeOffset? LastValidatedAt,
    string? LastValidationError,
    DateTimeOffset CreatedAt);
