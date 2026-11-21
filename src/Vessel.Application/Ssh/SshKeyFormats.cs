using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Vessel.Application.Ssh;

/// <summary>Parsed OpenSSH public key (<c>type base64 [comment]</c>).</summary>
public sealed record SshPublicKeyInfo(string Algorithm, string Base64Blob, int? Bits, string? Comment)
{
    /// <summary>The key without its comment, as written to <c>authorized_keys</c> and known_hosts.</summary>
    public string KeyLine => $"{Algorithm} {Base64Blob}";

    public string Fingerprint => SshKeyFormats.Fingerprint(Convert.FromBase64String(Base64Blob));
}

/// <summary>
///     Pure parsing and validation of SSH key material: strict private key acceptance rules (upstream Coolify v4.7:
///     PEM/OpenSSH armor only, no DSA, RSA ≥ 2048 bits, no passphrase), OpenSSH fingerprints and host key lines.
/// </summary>
public static class SshKeyFormats
{
    public const int MaxPrivateKeyLength = 16 * 1024;
    public const int MinimumRsaBits = 2048;

    public static readonly IReadOnlySet<string> AllowedKeyAlgorithms = new HashSet<string>(StringComparer.Ordinal)
    {
        "ssh-ed25519",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
        "ssh-rsa"
    };

    private static readonly HashSet<string> HostKeyAlgorithms = new(StringComparer.Ordinal)
    {
        "ssh-ed25519",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
        "ssh-rsa"
    };

    /// <summary>
    ///     Checks the text form of a pasted private key before it is handed to <c>ssh-keygen</c>. Returns the reason the
    ///     key cannot be used, or <c>null</c>.
    /// </summary>
    public static string? ValidatePrivateKeyText(string? privateKey)
    {
        if (string.IsNullOrWhiteSpace(privateKey)) return "A private key is required.";
        if (privateKey.Length > MaxPrivateKeyLength) return "The private key is too large.";
        if (privateKey.Contains('\0')) return "The private key contains invalid characters.";
        var text = privateKey.Trim();
        if (text.StartsWith("PuTTY-User-Key-File", StringComparison.Ordinal))
            return "PuTTY and other key formats are not supported. Paste the key in OpenSSH or PEM format. It starts with \"-----BEGIN\".";
        if (!text.StartsWith("-----BEGIN ", StringComparison.Ordinal))
            return "PuTTY and other key formats are not supported. Paste the key in OpenSSH or PEM format. It starts with \"-----BEGIN\".";
        if (text.Contains("PUBLIC KEY-----", StringComparison.Ordinal) ||
            text.Contains("CERTIFICATE-----", StringComparison.Ordinal))
            return "The private key is invalid. Paste an unencrypted private key, not a public key or a certificate.";
        if (text.StartsWith("-----BEGIN DSA PRIVATE KEY-----", StringComparison.Ordinal))
            return "DSA keys are not supported. Use an Ed25519 or RSA key.";
        if (text.Contains("ENCRYPTED", StringComparison.Ordinal))
            return "Encrypted private keys are not supported. Remove the passphrase first.";
        var lines = text.Split('\n', StringSplitOptions.TrimEntries);
        var header = lines[0];
        if (!header.EndsWith("PRIVATE KEY-----", StringComparison.Ordinal))
            return "The private key is invalid. Paste an unencrypted private key, not a public key or a certificate.";
        var footer = "-----END " + header["-----BEGIN ".Length..];
        if (!string.Equals(lines[^1], footer, StringComparison.Ordinal) ||
            lines.Count(line => line.StartsWith("-----", StringComparison.Ordinal)) != 2)
            return "The private key must contain exactly one key block.";
        return null;
    }

    /// <summary>Normalizes the text of a private key for storage and for <c>ssh-keygen</c> (LF line ends, final newline).</summary>
    public static string NormalizePrivateKey(string privateKey)
    {
        return privateKey.Trim().Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    public static SshPublicKeyInfo ParsePublicKey(string publicKeyLine)
    {
        if (string.IsNullOrWhiteSpace(publicKeyLine)) throw new FormatException("The public key is empty.");
        var fields = publicKeyLine.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2) throw new FormatException("The public key is not in OpenSSH format.");
        var algorithm = fields[0];
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(fields[1]);
        }
        catch (FormatException)
        {
            throw new FormatException("The public key is not in OpenSSH format.");
        }

        var reader = new WireReader(blob);
        var blobType = Encoding.ASCII.GetString(reader.ReadString());
        if (!string.Equals(blobType, algorithm, StringComparison.Ordinal))
            throw new FormatException("The public key type does not match its data.");

        int? bits = algorithm switch
        {
            "ssh-ed25519" => 256,
            "ecdsa-sha2-nistp256" => 256,
            "ecdsa-sha2-nistp384" => 384,
            "ecdsa-sha2-nistp521" => 521,
            "ssh-rsa" => RsaBits(reader),
            _ => null
        };
        var comment = fields.Length > 2 ? fields[2].Trim() : null;
        return new SshPublicKeyInfo(algorithm, fields[1], bits, string.IsNullOrEmpty(comment) ? null : comment);
    }

    /// <summary>Returns why a derived public key is not acceptable for server logins, or <c>null</c>.</summary>
    public static string? ValidatePublicKeyPolicy(SshPublicKeyInfo key)
    {
        if (key.Algorithm == "ssh-dss") return "DSA keys are not supported. Use an Ed25519 or RSA key.";
        if (!AllowedKeyAlgorithms.Contains(key.Algorithm))
            return "This key type is not supported. Use an Ed25519, ECDSA (nistp256, nistp384, or nistp521), or RSA key.";
        if (key.Algorithm == "ssh-rsa" && (key.Bits ?? 0) < MinimumRsaBits)
            return $"RSA keys must have at least {MinimumRsaBits} bits.";
        return null;
    }

    /// <summary>OpenSSH SHA-256 fingerprint of a public key blob (<c>SHA256:</c> + unpadded base64).</summary>
    public static string Fingerprint(byte[] blob)
    {
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
    }

    /// <summary>
    ///     Normalizes known_hosts style text to unique, sorted <c>type base64</c> lines. Host names, markers and comments
    ///     are dropped; lines whose data does not match their type are rejected. Returns <c>null</c> when nothing is left.
    /// </summary>
    public static string? NormalizeHostKeys(string? input)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in (input ?? string.Empty).Split('\n'))
        {
            var fields = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || fields[0].StartsWith('#') || fields[0].StartsWith('@')) continue;
            var offset = HostKeyAlgorithms.Contains(fields[0]) ? 0 : 1;
            if (fields.Length < offset + 2 || !HostKeyAlgorithms.Contains(fields[offset])) continue;
            try
            {
                SshPublicKeyInfo key = ParsePublicKey($"{fields[offset]} {fields[offset + 1]}");
                keys.Add(key.KeyLine);
            }
            catch (FormatException)
            {
                // Not a host key line.
            }
        }

        return keys.Count == 0 ? null : string.Join('\n', keys);
    }

    public static IReadOnlyList<string> HostKeyFingerprints(string? hostKeys)
    {
        return (hostKeys ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                try
                {
                    SshPublicKeyInfo key = ParsePublicKey(line);
                    return $"{key.Fingerprint} ({key.Algorithm})";
                }
                catch (FormatException)
                {
                    return null;
                }
            })
            .OfType<string>()
            .ToArray();
    }

    /// <summary>Known_hosts content for a server: every pinned key under the Vessel host key alias.</summary>
    public static string KnownHostsContent(string hostKeyAlias, string pinnedHostKeys)
    {
        var builder = new StringBuilder();
        foreach (var line in pinnedHostKeys.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            builder.Append(hostKeyAlias).Append(' ').Append(line).Append('\n');
        return builder.ToString();
    }

    /// <summary>True when OpenSSH reports that the server's host key does not match the pinned key.</summary>
    public static bool IsHostKeyMismatch(string? standardError)
    {
        return !string.IsNullOrEmpty(standardError) &&
               (standardError.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal) ||
                standardError.Contains("Host key verification failed", StringComparison.Ordinal));
    }

    private static int RsaBits(WireReader reader)
    {
        reader.ReadString(); // public exponent
        var modulus = reader.ReadString();
        return (int)new BigInteger(modulus, true, true).GetBitLength();
    }

    private sealed class WireReader(byte[] data)
    {
        private int _offset;

        public byte[] ReadString()
        {
            if (_offset + 4 > data.Length) throw new FormatException("The public key data is truncated.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_offset, 4));
            _offset += 4;
            if (length > data.Length - _offset) throw new FormatException("The public key data is truncated.");
            var value = data.AsSpan(_offset, (int)length).ToArray();
            _offset += (int)length;
            return value;
        }
    }
}
