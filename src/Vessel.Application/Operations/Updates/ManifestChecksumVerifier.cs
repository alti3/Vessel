using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vessel.Application.Operations.Updates;

/// <summary>
///     Integrity checks of the release manifest (OPS-01). The checksum list uses the <c>sha256sum</c> format
///     (<c>&lt;64 hex&gt;  &lt;file name&gt;</c>, optional <c>*</c> binary marker); the manifest's own file name must be
///     listed with the SHA-256 of the fetched bytes. Any malformed line rejects the whole list.
/// </summary>
public static class ManifestChecksumVerifier
{
    public const int MaxEntries = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyDictionary<string, string> ParseChecksumList(string checksumList)
    {
        ArgumentNullException.ThrowIfNull(checksumList);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in checksumList.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.Length < 67 || line[64] != ' ' || (line[65] != ' ' && line[65] != '*'))
                throw new ManifestVerificationException("The checksum list is malformed.");
            var hash = line[..64];
            if (!hash.All(char.IsAsciiHexDigit))
                throw new ManifestVerificationException("The checksum list contains an invalid SHA-256 digest.");
            var name = line[66..];
            if (name.Length == 0 || name.Length > 255 || name.Contains('/') || name.Contains('\\') ||
                name.Any(char.IsControl))
                throw new ManifestVerificationException("The checksum list contains an invalid file name.");
            if (!entries.TryAdd(name, hash.ToLowerInvariant()))
                throw new ManifestVerificationException("The checksum list lists a file more than once.");
            if (entries.Count > MaxEntries) throw new ManifestVerificationException("The checksum list is too long.");
        }

        if (entries.Count == 0) throw new ManifestVerificationException("The checksum list is empty.");
        return entries;
    }

    /// <summary>Verifies that <paramref name="content" /> hashes to the entry of <paramref name="fileName" />.</summary>
    public static void Verify(string checksumList, string fileName, ReadOnlySpan<byte> content)
    {
        IReadOnlyDictionary<string, string> entries = ParseChecksumList(checksumList);
        if (!entries.TryGetValue(fileName, out var expected))
            throw new ManifestVerificationException($"The checksum list has no entry for '{fileName}'.");
        Span<byte> actual = stackalloc byte[32];
        SHA256.HashData(content, actual);
        var expectedBytes = Convert.FromHexString(expected);
        if (!CryptographicOperations.FixedTimeEquals(actual, expectedBytes))
            throw new ManifestVerificationException($"The SHA-256 checksum of '{fileName}' does not match.");
    }

    /// <summary>Verifies the document against its checksum list and parses the manifest.</summary>
    public static VersionManifest VerifyAndParse(VersionManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var fileName = FileNameOf(document.ManifestUrl);
        Verify(document.ChecksumList, fileName, document.ManifestBytes);
        VersionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<VersionManifest>(Encoding.UTF8.GetString(document.ManifestBytes),
                JsonOptions);
        }
        catch (JsonException)
        {
            throw new ManifestVerificationException("The release manifest is not valid JSON.");
        }

        if (manifest?.Channels is null || manifest.SchemaVersion != 1)
            throw new ManifestVerificationException("The release manifest has an unsupported format.");
        return manifest;
    }

    public static string FileNameOf(Uri url)
    {
        var name = url.Segments.Length > 0 ? Uri.UnescapeDataString(url.Segments[^1]) : string.Empty;
        return string.IsNullOrWhiteSpace(name) || name.EndsWith('/') ? "releases.json" : name;
    }
}
