namespace Vessel.Infrastructure.Configuration;

/// <summary>
/// Secret encryption key ring configuration (section <c>Secrets</c>).
/// </summary>
/// <remarks>
/// The active key (<see cref="KeyVersion"/>) is resolved from, in order: <see cref="MasterKey"/>,
/// <see cref="KeyFile"/>, then <c>Keys:{KeyVersion}</c>. Previous keys stay in <see cref="Keys"/> so values
/// encrypted with them remain readable until the re-encryption command has rewritten them. Outside
/// Development a missing active key fails startup; in Development a per-install key file is generated
/// with owner-only permissions.
/// </remarks>
public sealed class SecretStorageOptions
{
    public const string SectionName = "Secrets";

    /// <summary>Base64-encoded 256-bit key for <see cref="KeyVersion"/>.</summary>
    public string? MasterKey { get; set; }

    /// <summary>The active key version label written with every new ciphertext.</summary>
    public string KeyVersion { get; set; } = "v1";

    /// <summary>
    /// Path to a root/service-only file (for example a systemd credential) containing the base64 key for
    /// <see cref="KeyVersion"/>. The file must not be readable by other users.
    /// </summary>
    public string? KeyFile { get; set; }

    /// <summary>Additional (typically previous) keys by version label, base64-encoded 256-bit values.</summary>
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Development only: where the generated per-install key is persisted. Defaults to the user's local
    /// application data directory.
    /// </summary>
    public string? DevelopmentKeyFile { get; set; }

    /// <summary>
    /// Migration aid for alpha installs that ran without a configured key: allows decrypting values written
    /// with the former built-in development key so the re-encryption command can rewrite them. Never used
    /// for encryption. Disable again once re-encryption has completed.
    /// </summary>
    public bool AllowLegacyBuiltInKeyDecryption { get; set; }
}
