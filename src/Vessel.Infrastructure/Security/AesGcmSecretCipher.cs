using System.Security.Cryptography;
using System.Text;
using Vessel.Application.Security;

namespace Vessel.Infrastructure.Security;

/// <summary>
/// AES-256-GCM over the versioned <see cref="SecretKeyRing"/>. Decryption selects the key by the recorded
/// key version; the optional legacy built-in key is only ever tried for decryption.
/// </summary>
public sealed class AesGcmSecretCipher(SecretKeyRing keyRing) : ISecretCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MaxPlaintextLength = 12000;
    private const string EnvelopePrefix = "vsec1";
    private const char EnvelopeSeparator = ':';

    public string ActiveKeyVersion => keyRing.ActiveVersion;

    public SecretCipherText Encrypt(string plaintext, string associatedData)
    {
        if (string.IsNullOrWhiteSpace(plaintext) || plaintext.Length > MaxPlaintextLength)
            throw new InvalidOperationException("Secret value is required and cannot exceed 12000 characters.");

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherText = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(keyRing.ActiveKey, TagSize);
            aes.Encrypt(nonce, plaintextBytes, cipherText, tag, Encoding.UTF8.GetBytes(associatedData));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }

        return new SecretCipherText(
            Convert.ToBase64String(cipherText),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            keyRing.ActiveVersion);
    }

    public string Decrypt(SecretCipherText cipherText, string associatedData)
    {
        byte[] data;
        byte[] nonce;
        byte[] tag;
        try
        {
            data = Convert.FromBase64String(cipherText.CipherText);
            nonce = Convert.FromBase64String(cipherText.Nonce);
            tag = Convert.FromBase64String(cipherText.Tag);
        }
        catch (FormatException ex)
        {
            throw new SecretDecryptionException("Stored secret ciphertext is malformed.", ex);
        }

        byte[] aad = Encoding.UTF8.GetBytes(associatedData);
        if (keyRing.TryGetKey(cipherText.KeyVersion, out byte[] key)
            && TryDecrypt(key, data, nonce, tag, aad, out string? plaintext))
            return plaintext!;

        if (keyRing.LegacyKey is not null && TryDecrypt(keyRing.LegacyKey, data, nonce, tag, aad, out plaintext))
            return plaintext!;

        throw new SecretDecryptionException(
            $"Secret encrypted with key version '{cipherText.KeyVersion}' cannot be decrypted with the configured key ring.");
    }

    public string Protect(string plaintext, string purpose)
    {
        SecretCipherText encrypted = Encrypt(plaintext, ProtectAssociatedData(purpose));
        return string.Join(EnvelopeSeparator, EnvelopePrefix, encrypted.KeyVersion, encrypted.Nonce, encrypted.Tag,
            encrypted.CipherText);
    }

    public string Unprotect(string protectedValue, string purpose)
    {
        if (!TryParseEnvelope(protectedValue, out SecretCipherText? envelope))
            throw new SecretDecryptionException("Protected value is not a Vessel secret envelope.");

        return Decrypt(envelope!, ProtectAssociatedData(purpose));
    }

    public bool IsProtected(string value)
    {
        return TryParseEnvelope(value, out _);
    }

    public string? GetProtectedKeyVersion(string value)
    {
        return TryParseEnvelope(value, out SecretCipherText? envelope) ? envelope!.KeyVersion : null;
    }

    private static bool TryParseEnvelope(string? value, out SecretCipherText? envelope)
    {
        envelope = null;
        if (string.IsNullOrEmpty(value) || !value.StartsWith(EnvelopePrefix + EnvelopeSeparator, StringComparison.Ordinal))
            return false;

        string[] parts = value.Split(EnvelopeSeparator);
        if (parts.Length != 5 || parts.Skip(1).Any(string.IsNullOrEmpty)) return false;

        envelope = new SecretCipherText(parts[4], parts[2], parts[3], parts[1]);
        return true;
    }

    private static bool TryDecrypt(byte[] key, byte[] data, byte[] nonce, byte[] tag, byte[] aad, out string? plaintext)
    {
        plaintext = null;
        if (nonce.Length != NonceSize || tag.Length != TagSize) return false;

        var plaintextBytes = new byte[data.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, data, tag, plaintextBytes, aad);
            plaintext = Encoding.UTF8.GetString(plaintextBytes);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    private static string ProtectAssociatedData(string purpose)
    {
        return "vessel-protect:" + purpose;
    }
}
