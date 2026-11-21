using Vessel.Domain;
using Vessel.Domain.Users;

namespace Vessel.Application.Security;

/// <summary>
/// Encrypts TOTP shared secrets at rest with the secret key ring. Values stored before encryption-at-rest
/// (plain base32 secrets) are still readable so existing enrollments keep working; they are upgraded on the
/// next successful verification or by the secret re-encryption command.
/// </summary>
public sealed class TwoFactorSecretProtector(ISecretCipher cipher)
{
    public string Protect(UserId userId, string plaintextSecret)
    {
        return cipher.Protect(plaintextSecret, Purpose(userId));
    }

    public string? Reveal(User user)
    {
        if (string.IsNullOrWhiteSpace(user.TwoFactorSecret)) return null;

        return cipher.IsProtected(user.TwoFactorSecret)
            ? cipher.Unprotect(user.TwoFactorSecret, Purpose(user.Id))
            : user.TwoFactorSecret;
    }

    /// <summary>True when the stored value is legacy plaintext or was encrypted with a non-active key.</summary>
    public bool NeedsRewrap(User user)
    {
        if (string.IsNullOrWhiteSpace(user.TwoFactorSecret)) return false;

        string? version = cipher.GetProtectedKeyVersion(user.TwoFactorSecret);
        return version is null || !string.Equals(version, cipher.ActiveKeyVersion, StringComparison.Ordinal);
    }

    /// <summary>Re-encrypts the stored secret with the active key. Returns false when nothing changed.</summary>
    public bool TryRewrap(User user, DateTimeOffset now)
    {
        if (!NeedsRewrap(user)) return false;

        string plaintext = Reveal(user)!;
        user.RewrapTwoFactorSecret(Protect(user.Id, plaintext), now);
        return true;
    }

    private static string Purpose(UserId userId)
    {
        return $"user-totp:{userId.Value:D}";
    }
}
