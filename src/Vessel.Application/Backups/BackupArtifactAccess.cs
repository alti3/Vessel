using System.Text.Json;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Application.Storage;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;

namespace Vessel.Application.Backups;

/// <summary>Opens clients for team backup destinations with credentials revealed from the secret vault.</summary>
public sealed class BackupDestinationConnector(ISecretVault secretVault, IBackupStorageProvider storage)
{
    public async Task<IBackupDestinationClient> ConnectAsync(BackupDestination destination,
        CancellationToken cancellationToken = default)
    {
        var json = await secretVault.RevealForDeploymentAsync(destination.TeamId, destination.CredentialsReferenceId,
            cancellationToken);
        BackupDestinationCredentials credentials = ParseCredentials(json);
        return storage.Connect(new BackupDestinationConnection(destination.Endpoint, destination.Region,
            destination.Bucket, destination.UsePathStyle, destination.SkipTlsVerification, credentials.AccessKeyId,
            credentials.SecretAccessKey));
    }

    public static string SerializeCredentials(string accessKeyId, string secretAccessKey)
    {
        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
            throw new DomainException("Access key ID and secret access key are required.");
        return JsonSerializer.Serialize(new BackupDestinationCredentials(accessKeyId.Trim(), secretAccessKey.Trim()));
    }

    private static BackupDestinationCredentials ParseCredentials(string json)
    {
        try
        {
            BackupDestinationCredentials? credentials = JsonSerializer.Deserialize<BackupDestinationCredentials>(json);
            if (credentials is { AccessKeyId.Length: > 0, SecretAccessKey.Length: > 0 }) return credentials;
        }
        catch (JsonException)
        {
            // Fall through: the stored value is not destination credentials.
        }

        throw new BackupDestinationUnavailableException("The stored destination credentials are unreadable.");
    }
}

/// <summary>
///     Reads a backup artifact from the best available copy: the local copy first, then the first remote replica whose
///     destination still exists.
/// </summary>
public sealed class BackupArtifactReader(
    IVesselDbContext dbContext,
    IBackupStorageProvider storage,
    BackupDestinationConnector connector)
{
    public async Task<Stream> OpenAsync(BackupExecution execution, CancellationToken cancellationToken = default)
    {
        if (execution.ArtifactKey is null) throw new DomainException("The backup has no artifact.");
        if (execution.HasLocalCopy)
            return await storage.Local.Storage.OpenReadAsync(
                new ObjectStorageKey(execution.ArtifactBucket!, execution.ArtifactKey), cancellationToken);

        foreach (BackupArtifactReplica replica in execution.Replicas.Where(replica => replica.IsAvailable))
        {
            BackupDestination? destination = dbContext.BackupDestinations.SingleOrDefault(item =>
                item.Id == replica.BackupDestinationId && item.TeamId == execution.TeamId);
            if (destination is null) continue;
            IBackupDestinationClient client = await connector.ConnectAsync(destination, cancellationToken);
            try
            {
                Stream content = await client.OpenReadAsync(
                    new ObjectStorageKey(replica.Bucket, execution.ArtifactKey), cancellationToken);
                return new OwningStream(content, client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        throw new DomainException("No copy of this backup is available any more.");
    }

    /// <summary>Disposes the destination client together with the download stream.</summary>
    private sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return inner.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            owner.Dispose();
            await base.DisposeAsync();
        }
    }
}
