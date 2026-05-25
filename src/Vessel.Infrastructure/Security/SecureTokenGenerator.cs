using System.Security.Cryptography;
using Vessel.Application.Security;

namespace Vessel.Infrastructure.Security;

internal sealed class SecureTokenGenerator : ITokenGenerator
{
    public string GenerateUrlSafeToken(int byteCount = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
    }
}
