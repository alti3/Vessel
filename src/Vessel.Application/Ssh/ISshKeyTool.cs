namespace Vessel.Application.Ssh;

public sealed record GeneratedSshKey(string PrivateKey, string PublicKey);

/// <summary>
///     Key material operations backed by OpenSSH (<c>ssh-keygen</c> through <c>IProcessRunner</c>). Private keys are
///     only ever written to owner-only temporary files that are deleted before the call returns.
/// </summary>
public interface ISshKeyTool
{
    /// <summary>Generates a new unencrypted Ed25519 key pair.</summary>
    Task<GeneratedSshKey> GenerateEd25519Async(string comment, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Derives the OpenSSH public key line from an unencrypted private key, or returns <c>null</c> when the key cannot
    ///     be loaded (invalid, encrypted, unsupported).
    /// </summary>
    Task<string?> DerivePublicKeyAsync(string privateKey, CancellationToken cancellationToken = default);
}

/// <summary>Reads the host key a server presents now (during key exchange, without logging in).</summary>
public interface ISshHostKeyScanner
{
    /// <summary>Returns normalized host key lines, or <c>null</c> plus an error when the server could not be read.</summary>
    Task<SshHostKeyScanResult> ScanAsync(
        string host,
        int port,
        string user,
        string hostKeyAlias,
        CancellationToken cancellationToken = default);
}

public sealed record SshHostKeyScanResult(string? HostKeys, string? Error);
