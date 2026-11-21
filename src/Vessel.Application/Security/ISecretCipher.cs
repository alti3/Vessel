namespace Vessel.Application.Security;

/// <summary>
/// Authenticated encryption backed by Vessel's versioned key ring. Every ciphertext records the key version
/// that produced it so decryption selects the right key and rotation can re-encrypt old values.
/// Implementations must never log, persist, or echo plaintext or key material.
/// </summary>
public interface ISecretCipher
{
    /// <summary>The key version used for all new encryptions.</summary>
    string ActiveKeyVersion { get; }

    /// <summary>Encrypts <paramref name="plaintext"/> with the active key, binding it to <paramref name="associatedData"/>.</summary>
    SecretCipherText Encrypt(string plaintext, string associatedData);

    /// <summary>Decrypts using the key named by <see cref="SecretCipherText.KeyVersion"/>.</summary>
    /// <exception cref="SecretDecryptionException">The key is unknown or authentication failed.</exception>
    string Decrypt(SecretCipherText cipherText, string associatedData);

    /// <summary>Encrypts into a single self-describing string (for columns such as the TOTP secret).</summary>
    string Protect(string plaintext, string purpose);

    /// <exception cref="SecretDecryptionException">The value is malformed, the key is unknown, or authentication failed.</exception>
    string Unprotect(string protectedValue, string purpose);

    /// <summary>True when <paramref name="value"/> is a protected envelope produced by <see cref="Protect"/>.</summary>
    bool IsProtected(string value);

    /// <summary>The key version recorded in a protected envelope, or null when the value is not protected.</summary>
    string? GetProtectedKeyVersion(string value);
}

public sealed record SecretCipherText(string CipherText, string Nonce, string Tag, string KeyVersion);

public sealed class SecretDecryptionException : InvalidOperationException
{
    public SecretDecryptionException()
        : base("Secret could not be decrypted with the configured key ring.")
    {
    }

    public SecretDecryptionException(string message)
        : base(message)
    {
    }

    public SecretDecryptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
