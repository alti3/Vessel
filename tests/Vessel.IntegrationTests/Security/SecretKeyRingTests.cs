using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Vessel.Application.Security;
using Vessel.Infrastructure.Configuration;
using Vessel.Infrastructure.Security;

namespace Vessel.IntegrationTests.Security;

/// <summary>SEC-04: no built-in fallback key, versioned keys, rotation, development key file.</summary>
public sealed class SecretKeyRingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vessel-keyring-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData(null)]
    public void MissingKey_FailsValidationOutsideDevelopment(string? environment)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["environment"] = environment })
            .Build();
        var validator = new SecretStorageOptionsValidator(SecretStorageOptionsValidator.IsDevelopment(configuration));

        ValidateOptionsResult result = validator.Validate(null, new SecretStorageOptions());

        Assert.True(result.Failed);
        Assert.Throws<InvalidOperationException>(() => SecretKeyRing.Create(new SecretStorageOptions(), false));
    }

    [Fact]
    public void Development_GeneratesPerInstallOwnerOnlyKeyFileAndReusesIt()
    {
        var options = new SecretStorageOptions { DevelopmentKeyFile = Path.Combine(_directory, "dev.key") };
        Assert.False(new SecretStorageOptionsValidator(true).Validate(null, options).Failed);

        SecretKeyRing first = SecretKeyRing.Create(options, isDevelopment: true);
        SecretKeyRing second = SecretKeyRing.Create(options, isDevelopment: true);

        Assert.True(File.Exists(options.DevelopmentKeyFile));
        Assert.Equal(first.ActiveKey, second.ActiveKey);
        Assert.NotEqual(SHA256.HashData("vessel-alpha-development-secret-storage-key"u8.ToArray()), first.ActiveKey);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(options.DevelopmentKeyFile));
    }

    [Fact]
    public void KeyFileReadableByOtherUsers_IsRejected()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_directory);
        string keyFile = Path.Combine(_directory, "master.key");
        File.WriteAllText(keyFile, NewKey());
        File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        ValidateOptionsResult result = new SecretStorageOptionsValidator(false)
            .Validate(null, new SecretStorageOptions { KeyFile = keyFile });

        Assert.True(result.Failed);

        File.SetUnixFileMode(keyFile, UnixFileMode.UserRead);
        Assert.False(new SecretStorageOptionsValidator(false)
            .Validate(null, new SecretStorageOptions { KeyFile = keyFile }).Failed);
    }

    [Fact]
    public void RotateThenDecrypt_SelectsKeyByVersion()
    {
        string oldKey = NewKey();
        var v1 = new AesGcmSecretCipher(SecretKeyRing.Create(
            new SecretStorageOptions { MasterKey = oldKey, KeyVersion = "v1" }, false));
        SecretCipherText old = v1.Encrypt("s3cret", "ref-1");
        string protectedTotp = v1.Protect("JBSWY3DPEHPK3PXP", "user-totp:1");

        var rotated = new AesGcmSecretCipher(SecretKeyRing.Create(new SecretStorageOptions
        {
            MasterKey = NewKey(),
            KeyVersion = "v2",
            Keys = new Dictionary<string, string> { ["v1"] = oldKey }
        }, false));

        Assert.Equal("v1", old.KeyVersion);
        Assert.Equal("s3cret", rotated.Decrypt(old, "ref-1"));
        Assert.Equal("JBSWY3DPEHPK3PXP", rotated.Unprotect(protectedTotp, "user-totp:1"));
        Assert.Equal("v2", rotated.Encrypt("new", "ref-2").KeyVersion);
        Assert.Throws<SecretDecryptionException>(() => rotated.Decrypt(old, "ref-other"));
        Assert.Throws<SecretDecryptionException>(() => rotated.Unprotect(protectedTotp, "user-totp:2"));

        var withoutOldKey = new AesGcmSecretCipher(SecretKeyRing.Create(
            new SecretStorageOptions { MasterKey = NewKey(), KeyVersion = "v2" }, false));
        Assert.Throws<SecretDecryptionException>(() => withoutOldKey.Decrypt(old, "ref-1"));
    }

    [Fact]
    public void LegacyBuiltInKey_IsOnlyUsedForDecryptionWhenExplicitlyEnabled()
    {
        byte[] legacy = SHA256.HashData("vessel-alpha-development-secret-storage-key"u8.ToArray());
        var legacyCipher = new AesGcmSecretCipher(SecretKeyRing.Create(
            new SecretStorageOptions { MasterKey = Convert.ToBase64String(legacy) }, false));
        SecretCipherText legacyValue = legacyCipher.Encrypt("alpha", "ref");

        var strict = new AesGcmSecretCipher(SecretKeyRing.Create(new SecretStorageOptions { MasterKey = NewKey() }, false));
        Assert.Throws<SecretDecryptionException>(() => strict.Decrypt(legacyValue, "ref"));

        var migrating = new AesGcmSecretCipher(SecretKeyRing.Create(
            new SecretStorageOptions { MasterKey = NewKey(), AllowLegacyBuiltInKeyDecryption = true }, false));
        Assert.Equal("alpha", migrating.Decrypt(legacyValue, "ref"));
        Assert.Throws<SecretDecryptionException>(() =>
            strict.Decrypt(migrating.Encrypt("fresh", "ref") with { KeyVersion = "v1" }, "ref"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
