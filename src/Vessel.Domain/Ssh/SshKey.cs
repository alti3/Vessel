using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Ssh;

/// <summary>
///     A team-owned SSH private key. The private material lives only in the secret vault
///     (<see cref="PrivateKeyReferenceId" />); the aggregate keeps the public key and its SHA-256 fingerprint so the key
///     can be shown, installed in <c>authorized_keys</c> and matched without ever revealing the private key.
/// </summary>
public sealed class SshKey : Entity<SshKeyId>
{
    public const int MaxPublicKeyLength = 16 * 1024;
    public const int MaxFingerprintLength = 128;
    public const int MaxAlgorithmLength = 64;

    private SshKey()
    {
    }

    private SshKey(
        SshKeyId id,
        TeamId teamId,
        ResourceName name,
        Description? description,
        SecretReferenceId privateKeyReferenceId,
        string publicKey,
        string fingerprint,
        string algorithm,
        int? bits,
        bool isGitRelated,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        Name = name;
        Description = description;
        PrivateKeyReferenceId = privateKeyReferenceId;
        PublicKey = DomainValidation.Required(publicKey, nameof(PublicKey), MaxPublicKeyLength);
        Fingerprint = DomainValidation.Required(fingerprint, nameof(Fingerprint), MaxFingerprintLength);
        Algorithm = DomainValidation.Required(algorithm, nameof(Algorithm), MaxAlgorithmLength);
        Bits = bits;
        IsGitRelated = isGitRelated;
        if (!Fingerprint.StartsWith("SHA256:", StringComparison.Ordinal))
            throw new DomainException("SSH key fingerprints must be SHA256 fingerprints.");
    }

    public TeamId TeamId { get; private set; }

    public ResourceName Name { get; private set; }

    public Description? Description { get; private set; }

    /// <summary>Vault reference holding the private key. Never exposed through APIs or DTOs.</summary>
    public SecretReferenceId PrivateKeyReferenceId { get; private set; }

    /// <summary>OpenSSH public key line (<c>type base64 [comment]</c>).</summary>
    public string PublicKey { get; private set; } = string.Empty;

    /// <summary>OpenSSH SHA-256 fingerprint (<c>SHA256:...</c>).</summary>
    public string Fingerprint { get; private set; } = string.Empty;

    /// <summary>OpenSSH key type, for example <c>ssh-ed25519</c>.</summary>
    public string Algorithm { get; private set; } = string.Empty;

    public int? Bits { get; private set; }

    /// <summary>True when the key is meant for Git deploy access rather than server logins.</summary>
    public bool IsGitRelated { get; private set; }

    public static SshKey Create(
        TeamId teamId,
        ResourceName name,
        Description? description,
        SecretReferenceId privateKeyReferenceId,
        string publicKey,
        string fingerprint,
        string algorithm,
        int? bits,
        bool isGitRelated,
        DateTimeOffset now)
    {
        return new SshKey(SshKeyId.New(), teamId, name, description, privateKeyReferenceId, publicKey, fingerprint,
            algorithm, bits, isGitRelated, now);
    }

    public void Update(ResourceName name, Description? description, bool isGitRelated, DateTimeOffset now)
    {
        Name = name;
        Description = description;
        IsGitRelated = isGitRelated;
        Touch(now);
    }
}
