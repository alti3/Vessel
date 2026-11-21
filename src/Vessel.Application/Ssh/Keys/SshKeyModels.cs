namespace Vessel.Application.Ssh.Keys;

/// <summary>A team SSH key as returned to clients: public data only, never the private key.</summary>
public sealed record SshKeySummary(
    Guid Id,
    string Name,
    string? Description,
    string Algorithm,
    int? Bits,
    string Fingerprint,
    string PublicKey,
    bool IsGitRelated,
    int ServerCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Imports an existing unencrypted private key (OpenSSH or PEM format).</summary>
public sealed record CreateSshKeyRequest(string Name, string? Description, string PrivateKey, bool IsGitRelated = false);

/// <summary>Generates a new Ed25519 key pair; only the public key is ever returned.</summary>
public sealed record GenerateSshKeyRequest(string Name, string? Description, bool IsGitRelated = false);

public sealed record UpdateSshKeyRequest(string Name, string? Description, bool IsGitRelated = false);

/// <summary>
///     Rotates every server that uses a key to a new key: the new public key is added to <c>authorized_keys</c> over the
///     old key, verified by logging in with the new key, the server switches to it, and the old key line is removed.
///     Without <see cref="NewKeyId" /> a fresh Ed25519 key is generated.
/// </summary>
public sealed record RotateSshKeyRequest(Guid? NewKeyId = null);

public sealed record SshKeyRotationAccepted(Guid OldKeyId, Guid NewKeyId, int ServerCount, string? JobId);

public enum SshKeyRotationStatus
{
    Moved,
    MovedOldKeyNotRemoved,
    NewKeyRejected,
    Unreachable,
    Failed
}

public sealed record SshKeyRotationServerResult(Guid ServerId, string ServerName, SshKeyRotationStatus Status,
    string Message);

/// <summary>Raised when a key cannot be deleted because servers still use it (HTTP 409).</summary>
public sealed class SshKeyInUseException(string message) : InvalidOperationException(message);
