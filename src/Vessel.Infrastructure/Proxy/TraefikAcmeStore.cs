using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Vessel.Application.Proxy;
using Vessel.Application.Proxy.Certificates;
using Vessel.Application.Servers;
using Vessel.Application.Servers.Files;

namespace Vessel.Infrastructure.Proxy;

/// <summary>
///     <see cref="ITraefikAcmeStore" /> over the server file channel (upstream <c>TraefikAcmeService</c> and the
///     <c>*TraefikAcme*</c> actions). Only certificate metadata is parsed out; private keys, account keys and certificate
///     bodies are never returned, logged or copied anywhere except into owner-only backups on the same server.
/// </summary>
public sealed partial class TraefikAcmeStore(IEnumerable<IServerFileChannel> fileChannels, TimeProvider timeProvider)
    : ITraefikAcmeStore
{
    private const string BackupTimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // acme.json is a private file read by Traefik's Go JSON decoder; keep base64 '+' and '/' readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<IReadOnlyList<AcmeCertificateEntry>> ListCertificatesAsync(
        ServerExecutionTarget target,
        CancellationToken cancellationToken = default)
    {
        var contents = await Channel(target).ReadTextAsync(target, ProxyLayout.AcmeStorageFile, cancellationToken);
        if (string.IsNullOrWhiteSpace(contents)) return [];
        return Parse(Decode(contents));
    }

    public async Task<AcmeWriteResult> DeleteCertificatesAsync(
        ServerExecutionTarget target,
        IReadOnlyCollection<string> certificateIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificateIds);
        if (certificateIds.Count == 0)
            throw new InvalidOperationException("No certificates were selected.");
        IServerFileChannel channel = Channel(target);
        var contents = await channel.ReadTextAsync(target, ProxyLayout.AcmeStorageFile, cancellationToken);
        if (string.IsNullOrWhiteSpace(contents))
            throw new InvalidOperationException("The selected certificate could not be found.");

        JsonObject root = Decode(contents);
        var pending = certificateIds.ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        foreach ((var resolver, JsonNode? resolverNode) in root)
        {
            if (resolverNode is not JsonObject resolverObject ||
                resolverObject["Certificates"] is not JsonArray certificates)
                continue;
            for (var index = certificates.Count - 1; index >= 0; index--)
            {
                if (certificates[index] is not JsonObject certificate) continue;
                var id = CertificateId(resolver, certificate);
                if (id is null || !pending.Remove(id)) continue;
                certificates.RemoveAt(index);
                removed++;
            }
        }

        if (pending.Count > 0)
            throw new InvalidOperationException("The selected certificate could not be found.");

        var backup = await WriteWithBackupAsync(target, channel, contents, root.ToJsonString(WriteOptions) + "\n",
            cancellationToken);
        return new AcmeWriteResult(removed, backup, true);
    }

    public async Task<IReadOnlyList<AcmeBackup>> ListBackupsAsync(
        ServerExecutionTarget target,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> names =
            await Channel(target).ListFilesAsync(target, ProxyLayout.AcmeBackupDirectory, cancellationToken);
        return names
            .Select(name => TryParseBackupName(name, out DateTimeOffset createdAt)
                ? new AcmeBackup(name, createdAt)
                : null)
            .OfType<AcmeBackup>()
            .OrderByDescending(backup => backup.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<AcmeWriteResult> RestoreBackupAsync(
        ServerExecutionTarget target,
        string backupName,
        CancellationToken cancellationToken = default)
    {
        var name = ValidateBackupName(backupName);
        IServerFileChannel channel = Channel(target);
        var backup = await channel.ReadTextAsync(target, $"{ProxyLayout.AcmeBackupDirectory}/{name}",
                         cancellationToken)
                     ?? throw new InvalidOperationException("The selected backup could not be found.");
        _ = Decode(backup); // never restore a file Traefik could not parse
        var current = await channel.ReadTextAsync(target, ProxyLayout.AcmeStorageFile, cancellationToken);
        var created = await WriteWithBackupAsync(target, channel, current, backup, cancellationToken);
        return new AcmeWriteResult(0, created, true);
    }

    public async Task DeleteBackupAsync(
        ServerExecutionTarget target,
        string backupName,
        CancellationToken cancellationToken = default)
    {
        var name = ValidateBackupName(backupName);
        if (!await Channel(target).DeleteFileAsync(target, $"{ProxyLayout.AcmeBackupDirectory}/{name}",
                cancellationToken))
            throw new InvalidOperationException("The selected backup could not be found.");
    }

    /// <summary>Parses certificate metadata from an acme.json document (exposed for tests).</summary>
    public static IReadOnlyList<AcmeCertificateEntry> Parse(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var entries = new List<AcmeCertificateEntry>();
        foreach ((var resolver, JsonNode? resolverNode) in root)
        {
            if (resolverNode is not JsonObject resolverObject ||
                resolverObject["Certificates"] is not JsonArray certificates)
                continue;
            foreach (JsonNode? node in certificates)
            {
                if (node is not JsonObject certificate) continue;
                var id = CertificateId(resolver, certificate);
                if (id is null) continue;
                var main = Text(certificate["domain"]?["main"])!;
                var sans = certificate["domain"]?["sans"] is JsonArray sanArray
                    ? sanArray.Select(Text).Where(name => !string.IsNullOrEmpty(name)).Select(name => name!)
                        .ToArray()
                    : [];
                (DateTimeOffset? notBefore, DateTimeOffset? notAfter) = Validity(Text(certificate["certificate"]));
                entries.Add(new AcmeCertificateEntry(id, resolver, main, sans, notBefore, notAfter));
            }
        }

        return entries;
    }

    public static JsonObject Decode(string contents)
    {
        try
        {
            return JsonNode.Parse(contents) as JsonObject
                   ?? throw new InvalidOperationException("The Traefik ACME file contains invalid JSON.");
        }
        catch (JsonException)
        {
            // The parser message can quote file content (key material); never surface it.
            throw new InvalidOperationException("The Traefik ACME file contains invalid JSON.");
        }
    }

    private async Task<string> WriteWithBackupAsync(
        ServerExecutionTarget target,
        IServerFileChannel channel,
        string? current,
        string next,
        CancellationToken cancellationToken)
    {
        await channel.EnsureDirectoryAsync(target, ProxyLayout.AcmeBackupDirectory,
            ServerFileModes.OwnerOnlyDirectory, cancellationToken);
        IReadOnlyList<AcmeBackup> existing = await ListBackupsAsync(target, cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        var name = BackupName(now);
        while (existing.Any(backup => backup.Name == name))
        {
            now = now.AddMilliseconds(1);
            name = BackupName(now);
        }

        if (current is not null)
            await channel.WriteAsync(target,
                new ServerFileWrite($"{ProxyLayout.AcmeBackupDirectory}/{name}", current,
                    ServerFileModes.OwnerReadWrite), cancellationToken);

        // Atomic replace (temporary file + rename) with mode 0600; Traefik refuses ACME storage readable by others.
        await channel.WriteAsync(target,
            new ServerFileWrite(ProxyLayout.AcmeStorageFile, next, ServerFileModes.OwnerReadWrite), cancellationToken);

        IReadOnlyList<AcmeBackup> backups = await ListBackupsAsync(target, cancellationToken);
        foreach (AcmeBackup stale in backups.Skip(ITraefikAcmeStore.MaxBackups))
            await channel.DeleteFileAsync(target, $"{ProxyLayout.AcmeBackupDirectory}/{stale.Name}",
                cancellationToken);
        return current is null ? string.Empty : name;
    }

    private static string? CertificateId(string resolver, JsonObject certificate)
    {
        var main = Text(certificate["domain"]?["main"]);
        if (string.IsNullOrEmpty(main)) return null;
        var sans = certificate["domain"]?["sans"] is JsonArray sanArray
            ? string.Join(',', sanArray.Select(Text).Where(name => !string.IsNullOrEmpty(name))
                .Order(StringComparer.Ordinal))
            : string.Empty;
        var body = Text(certificate["certificate"]) ?? string.Empty;
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{resolver}\0{main}\0{sans}\0{bodyHash}"))).ToLowerInvariant();
    }

    private static (DateTimeOffset? NotBefore, DateTimeOffset? NotAfter) Validity(string? encodedCertificate)
    {
        if (string.IsNullOrEmpty(encodedCertificate)) return (null, null);
        try
        {
            // Traefik stores the PEM chain base64-encoded; the first certificate is the leaf.
            var pem = Encoding.ASCII.GetString(Convert.FromBase64String(encodedCertificate));
            using X509Certificate2 certificate = X509Certificate2.CreateFromPem(pem);
            return (new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero),
                new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return (null, null);
        }
    }

    private static string? Text(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }

    private static string BackupName(DateTimeOffset now)
    {
        return $"acme-{now.UtcDateTime.ToString(BackupTimestampFormat, CultureInfo.InvariantCulture)}.json";
    }

    private static string ValidateBackupName(string backupName)
    {
        if (string.IsNullOrWhiteSpace(backupName) || !BackupNamePattern().IsMatch(backupName))
            throw new ArgumentException("Backup name is invalid.", nameof(backupName));
        return backupName;
    }

    private static bool TryParseBackupName(string name, out DateTimeOffset createdAt)
    {
        createdAt = default;
        if (!BackupNamePattern().IsMatch(name)) return false;
        return DateTimeOffset.TryParseExact(name["acme-".Length..^".json".Length], BackupTimestampFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out createdAt);
    }

    private IServerFileChannel Channel(ServerExecutionTarget target)
    {
        return fileChannels.For(target);
    }

    [GeneratedRegex(@"^acme-[0-9]{8}T[0-9]{9}Z\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupNamePattern();
}
