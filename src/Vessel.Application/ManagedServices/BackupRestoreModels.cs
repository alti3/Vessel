using Vessel.Application.Docker;
using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Domain.Databases;
using Vessel.Domain.Servers;

namespace Vessel.Application.ManagedServices;

/// <summary>
/// Lock keys shared by every operation that changes or reads a managed database's data: lifecycle actions
/// (start, stop, restart, delete), backups and restores all take the same key so they never overlap. Backups that
/// find the key held record a <see cref="BackupExecutionStatus.Skipped" /> execution instead of waiting.
/// </summary>
public static class DatabaseOperationLocks
{
    public static string KeyFor(DatabaseResourceId databaseId)
    {
        return $"database-op:{databaseId.Value:D}";
    }
}

public static class ManagedRuntimeTargets
{
    /// <summary>
    /// Resolves the container runtime (Docker or Podman) for a managed-service server. Managed services currently
    /// run only on local runtime targets.
    /// </summary>
    public static ContainerRuntimeTarget For(Server server)
    {
        if (server.ConnectionType == ServerConnectionType.Ssh)
            throw new DomainException("Managed services currently support local runtime targets.");
        return new ContainerRuntimeTarget(server.Runtime == ContainerRuntimeKind.Podman
            ? ContainerRuntimeProvider.Podman
            : ContainerRuntimeProvider.Docker);
    }
}

/// <summary>Raised when a database operation is requested while another one is queued or running (HTTP 409).</summary>
public sealed class DatabaseOperationConflictException(string message) : InvalidOperationException(message);

/// <summary>
///     One dump. <see cref="DatabaseName" /> selects a logical database (PostgreSQL defaults to <c>postgres</c>;
///     MySQL/MariaDB default to every user database); <see cref="Format" /> picks the dump format.
/// </summary>
public sealed record DatabaseBackupRequest(
    DatabaseResource Database,
    ContainerRuntimeTarget Runtime,
    string Credentials,
    BackupExecutionId ExecutionId,
    string? DatabaseName = null,
    BackupDumpFormat Format = BackupDumpFormat.Native);

/// <summary>Lists the logical user databases of a running managed database.</summary>
public sealed record DatabaseListRequest(
    DatabaseResource Database,
    ContainerRuntimeTarget Runtime,
    string Credentials);

/// <summary>
/// A finished backup artifact on owned local storage. <see cref="OpenRead" /> streams it for upload; disposing
/// the artifact deletes the temporary file.
/// </summary>
public sealed class BackupArtifactFile(
    string key,
    long sizeBytes,
    string sha256,
    string contentType,
    string format,
    Func<Stream> openRead,
    Func<ValueTask> cleanup) : IAsyncDisposable
{
    private bool _disposed;

    /// <summary>Object key used for every copy (local and remote) of this artifact.</summary>
    public string Key { get; } = key;

    public long SizeBytes { get; } = sizeBytes;

    /// <summary>Lower-case hex SHA-256 of the stored (compressed) artifact bytes.</summary>
    public string Sha256 { get; } = sha256;

    public string ContentType { get; } = contentType;

    /// <summary>Artifact format, for example <c>postgresql-custom</c>, <c>mysql-sql+gzip</c> or <c>redis-rdb+gzip</c>.</summary>
    public string Format { get; } = format;

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

public sealed record DatabaseRestoreOptions(
    bool DryRun,
    bool ReplaceExisting = false,
    bool KeepOwners = false);

public sealed record DatabaseRestoreRequest(
    DatabaseResource Target,
    ContainerRuntimeTarget Runtime,
    string Credentials,
    Stream Artifact,
    string? ExpectedSha256,
    DatabaseEngine SourceEngine,
    DatabaseRestoreOptions Options,
    Func<string, CancellationToken, Task> Log,
    string? DatabaseName = null);

public sealed record DatabaseRestoreOutcome(string Format, string Summary);

public sealed record QueueDatabaseRestoreRequest(
    bool DryRun,
    string Confirmation,
    bool ReplaceExisting = false,
    bool KeepOwners = false);

/// <summary>
///     A restore or import. <see cref="BackupExecutionId" /> is <see langword="null" /> for imports, which report their
///     <see cref="ImportSource" /> and <see cref="ImportReference" /> (upload ID, server path or object key) instead.
/// </summary>
public sealed record DatabaseRestoreSummary(
    Guid Id,
    Guid? BackupExecutionId,
    Guid TargetDatabaseId,
    DatabaseRestoreStatus Status,
    bool DryRun,
    bool ReplaceExisting,
    bool KeepOwners,
    string? DetectedFormat,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string Log,
    DatabaseImportSourceKind ImportSource = DatabaseImportSourceKind.None,
    string? ImportReference = null);
