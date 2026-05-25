using Vessel.Domain.Databases;

namespace Vessel.Application.ManagedServices;

public sealed class UnavailableDatabaseBackupProvider : IDatabaseBackupProvider
{
    public Task<BackupArtifact> BackupAsync(
        DatabaseResource database,
        string credentials,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Database backup provider is not configured.");
    }

    public Task<string> RestoreAsync(
        DatabaseResource target,
        string credentials,
        Stream artifact,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Database backup provider is not configured.");
    }
}
