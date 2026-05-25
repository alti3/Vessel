using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vessel.Application.Security;

public sealed class TotpService
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private readonly ITokenGenerator _tokenGenerator;

    public TotpService(ITokenGenerator tokenGenerator)
    {
        _tokenGenerator = tokenGenerator;
    }

    public string GenerateSecret()
    {
        return ToBase32(RandomNumberGenerator.GetBytes(20));
    }

    public string CreateOtpAuthUri(string issuer, string accountName, string secret)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30");
    }

    public bool VerifyCode(string secret, string code, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code)) return false;

        var normalized = code.Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        if (normalized.Length != 6 || normalized.Any(character => !char.IsDigit(character))) return false;

        var timestep = now.ToUnixTimeSeconds() / 30;
        for (long offset = -1; offset <= 1; offset++)
        {
            var candidate = ComputeCode(secret, timestep + offset);
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(candidate),
                    Encoding.ASCII.GetBytes(normalized)))
                return true;
        }

        return false;
    }

    public IReadOnlyList<string> GenerateRecoveryCodes(int count = 8)
    {
        return Enumerable.Range(0, count)
            .Select(_ => _tokenGenerator.GenerateUrlSafeToken(10))
            .ToArray();
    }

    private static string ComputeCode(string secret, long timestep)
    {
        var key = FromBase32(secret);
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitivesWriteInt64BigEndian(counter, timestep);

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counter.ToArray());
        var offset = hash[^1] & 0x0f;
        var binary =
            ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);

        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static string ToBase32(byte[] bytes)
    {
        var output = new StringBuilder((int)Math.Ceiling(bytes.Length / 5d) * 8);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                output.Append(Base32Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0) output.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 31]);

        return output.ToString();
    }

    private static byte[] FromBase32(string value)
    {
        var normalized = value.Replace("=", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        var bytes = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var character in normalized)
        {
            var index = Base32Alphabet.IndexOf(character, StringComparison.Ordinal);
            if (index < 0) throw new FormatException("Invalid base32 character.");

            buffer = (buffer << 5) | index;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsLeft - 8)) & 255));
                bitsLeft -= 8;
            }
        }

        return bytes.ToArray();
    }

    private static void BinaryPrimitivesWriteInt64BigEndian(Span<byte> destination, long value)
    {
        for (var index = 7; index >= 0; index--)
        {
            destination[index] = (byte)(value & 0xff);
            value >>= 8;
        }
    }
}
