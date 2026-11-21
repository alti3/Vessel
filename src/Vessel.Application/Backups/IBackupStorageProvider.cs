using Vessel.Application.Storage;

namespace Vessel.Application.Backups;

/// <summary>
///     Where backup copies are stored: the local copy on the control-plane host (always available, owned path) and
///     team-owned remote destinations, each reached through its own short-lived client.
/// </summary>
public interface IBackupStorageProvider
{
    /// <summary>The control-plane-local store that keeps each backup's local copy.</summary>
    BackupLocalStore Local { get; }

    /// <summary>
    ///     Opens a client for one remote destination. The endpoint is checked against the outbound request policy and
    ///     every connection is pinned to an allowed address. Dispose the client when done.
    /// </summary>
    IBackupDestinationClient Connect(BackupDestinationConnection connection);
}

public sealed record BackupLocalStore(IObjectStorage Storage, string Bucket);

/// <summary>Connection settings for one remote destination, including the revealed credentials.</summary>
public sealed record BackupDestinationConnection(
    string Endpoint,
    string Region,
    string Bucket,
    bool UsePathStyle,
    bool SkipTlsVerification,
    string AccessKeyId,
    string SecretAccessKey)
{
    /// <summary>Never print the secret key.</summary>
    public override string ToString()
    {
        return $"{Endpoint} ({Bucket})";
    }
}

public interface IBackupDestinationClient : IObjectStorage, IDisposable
{
    /// <summary>
    ///     Tests the connection by listing at most one object in the bucket. Throws
    ///     <see cref="BackupDestinationUnavailableException" /> with a message that is safe to store and show.
    /// </summary>
    Task ValidateAsync(CancellationToken cancellationToken = default);
}

/// <summary>A destination could not be reached or refused the request. The message never contains credentials.</summary>
public sealed class BackupDestinationUnavailableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>Credentials kept in the secret vault for a destination (serialized as JSON).</summary>
public sealed record BackupDestinationCredentials(string AccessKeyId, string SecretAccessKey);

/// <summary>Fallback when no backup storage is configured (tests, minimal hosts).</summary>
public sealed class UnavailableBackupStorageProvider : IBackupStorageProvider
{
    public BackupLocalStore Local { get; } = new(new UnavailableObjectStorage(), "local");

    public IBackupDestinationClient Connect(BackupDestinationConnection connection)
    {
        throw new BackupDestinationUnavailableException("Remote backup destinations are not available on this host.");
    }
}
