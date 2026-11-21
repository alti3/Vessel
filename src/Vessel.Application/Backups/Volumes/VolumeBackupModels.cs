using Vessel.Application.Servers;
using Vessel.Domain.Backups;
using Vessel.Domain.Storage;

namespace Vessel.Application.Backups.Volumes;

/// <summary>
///     What a helper container mounts at <c>/volume</c>: a named runtime volume, or an absolute host path inside the
///     owned resource directory (directory mounts).
/// </summary>
public sealed record VolumeArchiveSource(VolumeBackupTargetKind Kind, string Source);

/// <summary>A finished archive on owned local storage; disposing it deletes the file.</summary>
public sealed class VolumeArchive(long sizeBytes, string sha256, string? warning, Func<Stream> openRead,
    Func<ValueTask> cleanup) : IAsyncDisposable
{
    private bool _disposed;

    public long SizeBytes { get; } = sizeBytes;

    /// <summary>Lower-case hex SHA-256 of the archive bytes.</summary>
    public string Sha256 { get; } = sha256;

    /// <summary>Non-fatal note (for example, files changed while they were read).</summary>
    public string? Warning { get; } = warning;

    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return openRead();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await cleanup();
    }
}

/// <summary>
///     Archives and restores volumes with a short-lived helper container (pinned
///     <see cref="Common.ComponentVersions.VolumeBackupHelperImage" />) on the resource's server, through the runtime CLI
///     (Docker or Podman) over the server's command channel. Archives are tar+gzip streams that are hashed while they
///     are written to owned local storage; restores verify the checksum and gzip integrity before the target is touched.
/// </summary>
public interface IVolumeArchiveRunner
{
    Task<bool> SourceExistsAsync(ServerExecutionTarget server, VolumeArchiveSource source,
        CancellationToken cancellationToken = default);

    /// <summary>IDs of running containers that mount the source.</summary>
    Task<IReadOnlyList<string>> ListContainersUsingAsync(ServerExecutionTarget server, VolumeArchiveSource source,
        CancellationToken cancellationToken = default);

    Task StopContainersAsync(ServerExecutionTarget server, IReadOnlyList<string> containerIds,
        CancellationToken cancellationToken = default);

    Task StartContainersAsync(ServerExecutionTarget server, IReadOnlyList<string> containerIds,
        CancellationToken cancellationToken = default);

    Task<VolumeArchive> ArchiveAsync(ServerExecutionTarget server, VolumeArchiveSource source, Guid operationId,
        TimeSpan timeout, CancellationToken cancellationToken = default);

    Task RestoreAsync(ServerExecutionTarget server, VolumeArchiveSource source, Stream archive,
        string? expectedSha256, bool replaceExisting, Guid operationId, TimeSpan timeout,
        Func<string, CancellationToken, Task> log, CancellationToken cancellationToken = default);
}

public sealed record CreateVolumeBackupScheduleRequest(
    string CronExpression,
    bool Enabled = true,
    bool StopDuringBackup = false,
    bool DisableLocalCopy = false,
    int? TimeoutSeconds = null,
    BackupRetentionRequest? LocalRetention = null,
    BackupRetentionRequest? RemoteRetention = null,
    IReadOnlyList<Guid>? DestinationIds = null,
    int MissingAlertDays = 0);

public sealed record UpdateVolumeBackupScheduleRequest(
    string? CronExpression = null,
    bool? Enabled = null,
    bool? StopDuringBackup = null,
    bool? DisableLocalCopy = null,
    int? TimeoutSeconds = null,
    BackupRetentionRequest? LocalRetention = null,
    BackupRetentionRequest? RemoteRetention = null,
    IReadOnlyList<Guid>? DestinationIds = null,
    int? MissingAlertDays = null);

public sealed record VolumeBackupScheduleSummary(
    Guid Id,
    StorageOwnerKind OwnerKind,
    Guid OwnerId,
    VolumeBackupTargetKind TargetKind,
    Guid TargetId,
    string CronExpression,
    bool Enabled,
    bool StopDuringBackup,
    bool DisableLocalCopy,
    int TimeoutSeconds,
    BackupRetentionSummary LocalRetention,
    BackupRetentionSummary RemoteRetention,
    IReadOnlyList<Guid> DestinationIds,
    int MissingAlertDays,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? LastSucceededAt,
    DateTimeOffset? FailingSince);

/// <summary>
///     One run. Without <c>secrets.read</c> the failure, recovery and replica messages are hidden (they can carry tool
///     output) and <see cref="MessageHidden" /> is set.
/// </summary>
public sealed record VolumeBackupExecutionSummary(
    Guid Id,
    Guid ScheduleId,
    VolumeBackupStatus Status,
    bool Manual,
    long? SizeBytes,
    string? ChecksumSha256,
    bool HasLocalCopy,
    IReadOnlyList<BackupReplicaSummary> Replicas,
    string? FailureReason,
    string? Warning,
    bool StopRecoveryPending,
    int RecoveryAttempts,
    bool RecoveryNeedsAttention,
    string? RecoveryError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    bool MessageHidden);

public sealed record PagedVolumeBackupExecutions(
    IReadOnlyList<VolumeBackupExecutionSummary> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>Restores an archive into its volume or directory; <see cref="Confirmation" /> must be <c>RESTORE</c>.</summary>
public sealed record QueueVolumeRestoreRequest(string Confirmation, bool ReplaceExisting = true);

public sealed record VolumeBackupRestoreSummary(
    Guid Id,
    Guid ExecutionId,
    VolumeRestoreStatus Status,
    bool ReplaceExisting,
    string? FailureReason,
    bool StopRecoveryPending,
    bool RecoveryNeedsAttention,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string Log);
