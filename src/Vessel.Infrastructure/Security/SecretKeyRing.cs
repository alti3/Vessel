using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vessel.Infrastructure.Configuration;
using Vessel.Infrastructure.Files;

namespace Vessel.Infrastructure.Security;

/// <summary>
/// Versioned secret encryption keys. There is no built-in fallback key: outside Development the active key
/// must be configured (startup validation enforces this); in Development a random per-install key is
/// generated once and persisted with owner-only permissions.
/// </summary>
public sealed partial class SecretKeyRing
{
    public const int KeySizeBytes = 32;

    private const string LegacyBuiltInKeySeed = "vessel-alpha-development-secret-storage-key";

    private readonly Dictionary<string, byte[]> _keys;

    private SecretKeyRing(string activeVersion, Dictionary<string, byte[]> keys, byte[]? legacyKey)
    {
        ActiveVersion = activeVersion;
        _keys = keys;
        LegacyKey = legacyKey;
    }

    public string ActiveVersion { get; }

    public IReadOnlyCollection<string> Versions => _keys.Keys;

    /// <summary>The former built-in key, available for decryption only when explicitly enabled.</summary>
    internal byte[]? LegacyKey { get; }

    public byte[] ActiveKey => _keys[ActiveVersion];

    public bool TryGetKey(string version, out byte[] key)
    {
        return _keys.TryGetValue(version, out key!);
    }

    public static SecretKeyRing Create(SecretStorageOptions options, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<string> errors = Validate(options, isDevelopment);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));

        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (version, value) in options.Keys)
            keys[version] = DecodeKey(value);

        byte[]? active = ResolveConfiguredActiveKey(options);
        if (active is null)
        {
            // Validation guarantees this branch is Development-only.
            active = LoadOrCreateDevelopmentKey(DevelopmentKeyPath(options));
        }

        keys[options.KeyVersion] = active;
        byte[]? legacy = options.AllowLegacyBuiltInKeyDecryption
            ? SHA256.HashData(Encoding.UTF8.GetBytes(LegacyBuiltInKeySeed))
            : null;
        return new SecretKeyRing(options.KeyVersion, keys, legacy);
    }

    /// <summary>Validates configuration without side effects (no key generation).</summary>
    public static IReadOnlyList<string> Validate(SecretStorageOptions options, bool isDevelopment)
    {
        List<string> errors = [];
        if (!IsValidVersion(options.KeyVersion))
            errors.Add("Secrets:KeyVersion must be 1-40 characters of letters, digits, '.', '_' or '-'.");

        if (!string.IsNullOrWhiteSpace(options.MasterKey) && !IsValidKey(options.MasterKey))
            errors.Add("Secrets:MasterKey must be a base64-encoded 256-bit key.");

        foreach (var (version, value) in options.Keys)
        {
            if (!IsValidVersion(version))
                errors.Add($"Secrets:Keys contains an invalid version label '{Sanitize(version)}'.");
            if (!IsValidKey(value))
                errors.Add($"Secrets:Keys:{Sanitize(version)} must be a base64-encoded 256-bit key.");
        }

        if (!string.IsNullOrWhiteSpace(options.KeyFile))
        {
            string? keyFileError = ValidateKeyFile(options.KeyFile);
            if (keyFileError is not null) errors.Add(keyFileError);
        }

        bool hasActive = !string.IsNullOrWhiteSpace(options.MasterKey)
                         || !string.IsNullOrWhiteSpace(options.KeyFile)
                         || options.Keys.ContainsKey(options.KeyVersion);
        if (!hasActive && !isDevelopment)
            errors.Add(
                "No secret encryption key is configured. Set Secrets:MasterKey, Secrets:KeyFile, or Secrets:Keys:<KeyVersion> " +
                "to a base64-encoded 256-bit key (for example from a root-only file or systemd credential). " +
                "Vessel no longer falls back to a built-in key outside Development.");

        return errors;
    }

    public static string DevelopmentKeyPath(SecretStorageOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DevelopmentKeyFile))
            return Path.GetFullPath(options.DevelopmentKeyFile);

        string baseDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        if (string.IsNullOrWhiteSpace(baseDirectory))
            baseDirectory = Path.Combine(AppContext.BaseDirectory, "storage");

        return Path.Combine(baseDirectory, "Vessel", "secrets", "development-master.key");
    }

    private static byte[]? ResolveConfiguredActiveKey(SecretStorageOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.MasterKey))
            return DecodeKey(options.MasterKey);
        if (!string.IsNullOrWhiteSpace(options.KeyFile))
            return DecodeKey(File.ReadAllText(options.KeyFile));
        return options.Keys.TryGetValue(options.KeyVersion, out var value) ? DecodeKey(value) : null;
    }

    private static byte[] LoadOrCreateDevelopmentKey(string path)
    {
        if (File.Exists(path))
        {
            string? error = ValidateKeyFile(path);
            if (error is not null) throw new InvalidOperationException(error);
            return DecodeKey(File.ReadAllText(path));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] key = RandomNumberGenerator.GetBytes(KeySizeBytes);
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            using (var stream = new FileStream(path, fileOptions))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(Convert.ToBase64String(key));
            }

            OwnerOnlyFilePermissions.Apply(path);
            return key;
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process created the key concurrently; use it.
            return DecodeKey(File.ReadAllText(path));
        }
    }

    private static string? ValidateKeyFile(string path)
    {
        if (!File.Exists(path))
            return "Secrets:KeyFile does not exist or is not readable.";

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            const UnixFileMode otherAccess = UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & otherAccess) != 0)
                return "The secret key file must not be accessible by other users (expected mode 0600 or 0400).";
        }

        try
        {
            return IsValidKey(File.ReadAllText(path))
                ? null
                : "The secret key file must contain a base64-encoded 256-bit key.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The secret key file could not be read.";
        }
    }

    private static bool IsValidKey(string value)
    {
        try
        {
            return Convert.FromBase64String(value.Trim()).Length == KeySizeBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] DecodeKey(string value)
    {
        return Convert.FromBase64String(value.Trim());
    }

    private static bool IsValidVersion(string? version)
    {
        return !string.IsNullOrWhiteSpace(version) && VersionPattern().IsMatch(version);
    }

    private static string Sanitize(string value)
    {
        return value.Length > 40 ? value[..40] : value;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
