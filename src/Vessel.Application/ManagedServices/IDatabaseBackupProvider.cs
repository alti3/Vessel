namespace Vessel.Application.ManagedServices;

/// <summary>
/// Produces and restores database backup artifacts through the container runtime abstraction. Implementations
/// stream dumps through owned temporary files and never buffer whole artifacts in memory.
/// </summary>
public interface IDatabaseBackupProvider
{
    /// <summary>
    /// Dumps the database to an owned temporary file (compressed and hashed while streaming). The caller uploads
    /// the file and must dispose the returned artifact to delete it.
    /// </summary>
    Task<BackupArtifactFile> BackupAsync(
        DatabaseBackupRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies, inspects and (unless <see cref="DatabaseRestoreOptions.DryRun" />) restores an artifact. The
    /// artifact checksum, compression, format and engine compatibility are checked before the target is changed.
    /// </summary>
    Task<DatabaseRestoreOutcome> RestoreAsync(
        DatabaseRestoreRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Lists the logical user databases (PostgreSQL: every connectable non-template database), used to expand an
    ///     "all databases" schedule into one backup per database. Engines without logical databases return none.
    /// </summary>
    Task<IReadOnlyList<string>> ListDatabasesAsync(
        DatabaseListRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<string>>([]);
    }
}
