using System.Security.Cryptography;
using System.Text;
using Vessel.Application.Security;
using Vessel.Domain;

namespace Vessel.Application.Proxy;

/// <summary>
///     Encrypts stored proxy configuration text (versions and user dynamic files) with the secret key ring. The purpose
///     binds each ciphertext to its server, so a value copied to another server's row fails authentication. Rows written
///     before encryption at rest (plaintext) remain readable.
/// </summary>
public sealed class ProxyConfigurationProtector(ISecretCipher cipher)
{
    public string Protect(ServerId serverId, string plaintext)
    {
        return cipher.Protect(plaintext, Purpose(serverId));
    }

    /// <exception cref="SecretDecryptionException">The stored value cannot be decrypted with the key ring.</exception>
    public string Reveal(ServerId serverId, string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return cipher.IsProtected(stored) ? cipher.Unprotect(stored, Purpose(serverId)) : stored;
    }

    public static string Hash(string plaintext)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();
    }

    private static string Purpose(ServerId serverId)
    {
        return $"proxy-configuration:{serverId.Value:D}";
    }
}
