using System.Text.Json.Serialization;
using Vessel.Domain;
using Vessel.Domain.Backups;

namespace Vessel.Application.ManagedServices.DatabaseImports;

/// <summary>
///     Owned staging storage for chunked import uploads, plus validated reads of dump files on the database server's
///     host. Implemented in Infrastructure; every path is owned (validated, symlink-checked) and staged files are
///     owner-only.
/// </summary>
public interface IDatabaseImportStaging
{
    /// <summary>Largest accepted upload or server-path file (default 10 GiB, upstream <c>MAX_BYTES</c>).</summary>
    long MaxImportBytes { get; }

    /// <summary>
    ///     Appends at most <paramref name="maxBytes" /> bytes from <paramref name="content" /> at <paramref name="offset" />
    ///     (the current staged length). Returns the bytes written; throws when the chunk is larger than allowed (the
    ///     staged file is truncated back to <paramref name="offset" />).
    /// </summary>
    Task<long> AppendAsync(DatabaseImportUploadId uploadId, long offset, Stream content, long maxBytes,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(DatabaseImportUploadId uploadId, CancellationToken cancellationToken = default);

    Task DeleteAsync(DatabaseImportUploadId uploadId, CancellationToken cancellationToken = default);

    /// <summary>Upload IDs that have staged bytes (for orphan cleanup).</summary>
    IReadOnlyList<Guid> ListStaged();

    /// <summary>
    ///     Validates a path on the control-plane host for a server-path import: absolute, normalized, an existing regular
    ///     file (after resolving links) inside a configured import root when roots are configured, and within
    ///     <see cref="MaxImportBytes" />. Returns the normalized path.
    /// </summary>
    string ValidateServerPath(string path);

    Task<Stream> OpenServerFileAsync(string path, CancellationToken cancellationToken = default);
}

public sealed record StartDatabaseImportUploadRequest(string FileName, long SizeBytes);

public sealed record DatabaseImportUploadSummary(
    Guid Id,
    Guid DatabaseId,
    string FileName,
    long SizeBytes,
    long ReceivedBytes,
    bool Completed,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    long MaxChunkBytes);

[JsonConverter(typeof(JsonStringEnumConverter<DatabaseImportSourceRequestKind>))]
public enum DatabaseImportSourceRequestKind
{
    Upload,
    Server,
    Destination
}

/// <summary>
///     Starts an import. <see cref="Source" /> selects the input: <c>upload</c> (<see cref="UploadId" />), <c>server</c>
///     (<see cref="Path" /> on the database server host; instance administrators only) or <c>destination</c>
///     (<see cref="DestinationId" /> and object <see cref="Path" />). A real import needs <see cref="Confirmation" /> =
///     <c>RESTORE</c>; <see cref="DryRun" /> only verifies and inspects the dump.
/// </summary>
public sealed record QueueDatabaseImportRequest(
    DatabaseImportSourceRequestKind Source,
    Guid? UploadId = null,
    string? Path = null,
    Guid? DestinationId = null,
    bool DryRun = false,
    string? Confirmation = null,
    bool ReplaceExisting = false,
    bool KeepOwners = false,
    string? DatabaseName = null);
