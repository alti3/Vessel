using System.Security.Cryptography;
using System.Text;

namespace Vessel.Application.Security;

public static class TokenHashing
{
    public static string Sha256(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
