using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Vessel.Application.Auth;
using Vessel.Application.Authorization;
using Vessel.Application.Deployments;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Secrets;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Application.SecurityHardening;

/// <summary>SEC-04: TOTP secrets at rest and key-rotation re-encryption.</summary>
public sealed class SecretKeyManagementTests
{
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly SecurityTestDbContext _db = new();

    [Fact]
    public async Task TwoFactorSetup_StoresEncryptedSecret_AndConfirmationStillVerifies()
    {
        User user = AddUser(instanceAdmin: false);
        var cipher = new FakeSecretCipher("v1");
        VesselAuthenticationService auth = Auth(cipher);

        TwoFactorSetup setup = await auth.StartTwoFactorSetupAsync(user.Id, "Vessel",
            TestContext.Current.CancellationToken);

        Assert.NotEqual(setup.Secret, user.TwoFactorSecret);
        Assert.DoesNotContain(setup.Secret, user.TwoFactorSecret!, StringComparison.Ordinal);
        Assert.True(cipher.IsProtected(user.TwoFactorSecret!));

        TwoFactorRecoveryCodes? codes = await auth.ConfirmTwoFactorAsync(user.Id, Totp(setup.Secret), null,
            TestContext.Current.CancellationToken);
        Assert.NotNull(codes);
    }

    [Fact]
    public async Task Login_UpgradesLegacyPlaintextTotpSecret()
    {
        User user = AddUser(instanceAdmin: false);
        user.SetPasswordHash("hash:pw", false, _now);
        string plaintext = new TotpService(new FixedTokenGenerator()).GenerateSecret();
        user.StageTwoFactorSecret(plaintext, _now);
        user.ConfirmTwoFactor("[]", _now);
        var cipher = new FakeSecretCipher("v1");

        AuthenticatedUser? result = await Auth(cipher).LoginAsync(user.Email.Value, "pw", Totp(plaintext), null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result.TwoFactorRequired);
        Assert.True(cipher.IsProtected(user.TwoFactorSecret!));
        Assert.Equal(plaintext, new TwoFactorSecretProtector(cipher).Reveal(user));
    }

    [Fact]
    public async Task Reencryption_RewritesOldKeyValues_AndIsIdempotent()
    {
        User admin = AddUser(instanceAdmin: true);
        var oldCipher = new FakeSecretCipher("v1");
        var reference = SecretReferenceId.New();
        SecretCipherText old = oldCipher.Encrypt("db-password", SecretValueAssociatedData.For(reference));
        SecretValue value = SecretValue.Create(reference, old.CipherText, old.Nonce, old.Tag, old.KeyVersion, _now);
        _db.SecretValueItems.Add(value);
        admin.StageTwoFactorSecret(oldCipher.Protect("JBSWY3DPEHPK3PXP", $"user-totp:{admin.Id.Value:D}"), _now);

        var rotated = new FakeSecretCipher("v2", "v1");
        SecretReencryptionService service = Reencryption(rotated);

        SecretReencryptionResult dryRun = await service.ReencryptAsync(admin.Id, dryRun: true,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, dryRun.SecretValuesUsingOldKey);
        Assert.Equal(0, dryRun.SecretValuesReencrypted);
        Assert.Equal("v1", value.KeyVersion);

        SecretReencryptionResult result = await service.ReencryptAsync(admin.Id, dryRun: false,
            TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.SecretValuesReencrypted);
        Assert.Equal(1, result.TwoFactorSecretsReencrypted);
        Assert.Equal("v2", value.KeyVersion);
        Assert.Equal("v2", rotated.GetProtectedKeyVersion(admin.TwoFactorSecret!));

        // After rotation the old key can be dropped: values decrypt with the new key only.
        var newOnly = new FakeSecretCipher("v2");
        Assert.Equal("db-password", newOnly.Decrypt(
            new SecretCipherText(value.CipherText, value.Nonce, value.Tag, value.KeyVersion),
            SecretValueAssociatedData.For(reference)));

        SecretReencryptionResult second = await service.ReencryptAsync(admin.Id, dryRun: false,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, second.SecretValuesUsingOldKey);
        Assert.Equal(0, second.TwoFactorSecretsReencrypted);
    }

    [Fact]
    public async Task Reencryption_ReportsUndecryptableValuesWithoutChangingThem()
    {
        User admin = AddUser(instanceAdmin: true);
        var reference = SecretReferenceId.New();
        SecretCipherText lost = new FakeSecretCipher("v0").Encrypt("x", SecretValueAssociatedData.For(reference));
        SecretValue value = SecretValue.Create(reference, lost.CipherText, lost.Nonce, lost.Tag, lost.KeyVersion, _now);
        _db.SecretValueItems.Add(value);

        SecretReencryptionResult result = await Reencryption(new FakeSecretCipher("v2"))
            .ReencryptAsync(admin.Id, dryRun: false, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Undecryptable);
        Assert.Equal("v0", value.KeyVersion);
    }

    [Fact]
    public async Task Reencryption_RequiresInstanceAdmin()
    {
        User owner = AddUser(instanceAdmin: false);
        SecretReencryptionService service = Reencryption(new FakeSecretCipher("v1"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ReencryptAsync(owner.Id, dryRun: true, TestContext.Current.CancellationToken));
        Assert.Throws<UnauthorizedAccessException>(() => service.QueueReencryption(owner.Id));
    }

    private User AddUser(bool instanceAdmin)
    {
        User user = User.Create(new DisplayName("user"), new EmailAddress($"u{_db.UserItems.Count}@example.com"), _now);
        if (instanceAdmin) user.GrantInstanceAdmin(_now);
        _db.UserItems.Add(user);
        _db.TeamItems.Add(Team.Create(new DisplayName("team"), user.Id, true, _now));
        return user;
    }

    private VesselAuthenticationService Auth(ISecretCipher cipher) =>
        new(_db, new PlainPasswordHasher(), new FixedTokenGenerator(), new TotpService(new FixedTokenGenerator()),
            new RecordingAuditWriter(), Options.Create(new AuthOptions()), cipher);

    private SecretReencryptionService Reencryption(ISecretCipher cipher) =>
        new(_db, new VesselAuthorizationService(_db), cipher, new InMemoryDistributedLockManager(TimeProvider.System),
            new RecordingJobDispatcher(), new RecordingAuditWriter(), TimeProvider.System);

    /// <summary>RFC 6238 TOTP (SHA1, 6 digits, 30 s) for test codes.</summary>
    private static string Totp(string base32Secret)
    {
        byte[] key = FromBase32(base32Secret);
        long step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        byte[] counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
#pragma warning disable CA5350 // TOTP is defined over HMAC-SHA1.
        byte[] hash = HMACSHA1.HashData(key, counter);
#pragma warning restore CA5350
        int offset = hash[^1] & 0x0f;
        int binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] FromBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0;
        int bits = 0;
        foreach (char character in value.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character, StringComparison.Ordinal);
            bits += 5;
            if (bits < 8) continue;
            output.Add((byte)(buffer >> (bits - 8)));
            bits -= 8;
        }

        return [.. output];
    }

    private sealed class PlainPasswordHasher : IPasswordHasher
    {
        public string HashPassword(string password) => "hash:" + password;
        public bool VerifyPassword(string passwordHash, string password) => passwordHash == "hash:" + password;
    }

    private sealed class FixedTokenGenerator : ITokenGenerator
    {
        private int _counter;
        public string GenerateUrlSafeToken(int byteCount = 32) => "token" + Interlocked.Increment(ref _counter);
    }
}
