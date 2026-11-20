namespace Vessel.Application.ManagedServices;

public sealed class UnavailableDatabaseBackupProvider : IDatabaseBackupProvider
{
    public Task<BackupArtifactFile> BackupAsync(
        DatabaseBackupRequest request,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Database backup provider is not configured.");
    }

    public Task<DatabaseRestoreOutcome> RestoreAsync(
        DatabaseRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Database backup provider is not configured.");
    }
}
