using Vessel.Domain.Databases;

namespace Vessel.Application.ManagedServices;

public interface IDatabaseBackupProvider
{
    Task<BackupArtifact> BackupAsync(
        DatabaseResource database,
        string credentials,
        CancellationToken cancellationToken = default);

    Task<string> RestoreAsync(
        DatabaseResource target,
        string credentials,
        Stream artifact,
        bool dryRun,
        CancellationToken cancellationToken = default);
}
