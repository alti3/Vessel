using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Vessel.Application.Proxy;
using Vessel.Application.Proxy.Certificates;
using Vessel.Application.Servers;
using Vessel.Application.Servers.Files;
using Vessel.Domain;
using Vessel.Domain.Servers;
using Vessel.Infrastructure.Proxy;

namespace Vessel.IntegrationTests.Proxy;

public sealed class TraefikAcmeStoreTests
{
    private const string PrivateKeyMarker = "PRIVATE-KEY-MATERIAL";

    private static readonly ServerExecutionTarget Target = new(ServerId.New(), ServerConnectionType.Ssh, "10.0.0.2",
        22, "root", ContainerRuntimeKind.Docker);

    [Fact]
    public async Task ListCertificates_ParsesMainSansAndExpiryWithoutKeyMaterial()
    {
        var files = new MemoryFileChannel();
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.AddDays(60);
        files.Files[ProxyLayout.AcmeStorageFile] = AcmeJson(("letsencrypt", "app.example.com", ["www.example.com"],
            notAfter));
        var store = new TraefikAcmeStore([files], TimeProvider.System);

        AcmeCertificateEntry entry = Assert.Single(await store.ListCertificatesAsync(Target,
            TestContext.Current.CancellationToken));

        Assert.Equal("letsencrypt", entry.Resolver);
        Assert.Equal("app.example.com", entry.MainDomain);
        Assert.Equal(["www.example.com"], entry.SubjectAlternativeNames);
        Assert.NotNull(entry.ExpiresAt);
        Assert.True(Math.Abs((entry.ExpiresAt!.Value - notAfter).TotalMinutes) < 2);
        Assert.Equal(64, entry.Id.Length);
        Assert.DoesNotContain(PrivateKeyMarker, entry.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListCertificates_EmptyOrMissingStorageHasNoCertificates()
    {
        var files = new MemoryFileChannel();
        var store = new TraefikAcmeStore([files], TimeProvider.System);
        Assert.Empty(await store.ListCertificatesAsync(Target, TestContext.Current.CancellationToken));
        files.Files[ProxyLayout.AcmeStorageFile] = "";
        Assert.Empty(await store.ListCertificatesAsync(Target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidJson_ErrorNeverQuotesFileContents()
    {
        var files = new MemoryFileChannel();
        files.Files[ProxyLayout.AcmeStorageFile] = "{\"key\": \"" + PrivateKeyMarker + "\" broken";
        var store = new TraefikAcmeStore([files], TimeProvider.System);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ListCertificatesAsync(Target, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(PrivateKeyMarker, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteCertificates_BacksUpFirstAndWritesOwnerOnlyFile()
    {
        var files = new MemoryFileChannel();
        var original = AcmeJson(("letsencrypt", "keep.example.com", [], DateTimeOffset.UtcNow.AddDays(60)),
            ("letsencrypt", "drop.example.com", [], DateTimeOffset.UtcNow.AddDays(60)));
        files.Files[ProxyLayout.AcmeStorageFile] = original;
        var store = new TraefikAcmeStore([files], TimeProvider.System);
        IReadOnlyList<AcmeCertificateEntry> entries =
            await store.ListCertificatesAsync(Target, TestContext.Current.CancellationToken);

        AcmeWriteResult result = await store.DeleteCertificatesAsync(Target,
            [entries.Single(entry => entry.MainDomain == "drop.example.com").Id],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.RemovedCertificates);
        Assert.True(result.RestartRequired);
        Assert.Equal(original, files.Files[$"{ProxyLayout.AcmeBackupDirectory}/{result.BackupName}"]);
        Assert.Equal(ServerFileModes.OwnerReadWrite, files.Modes[ProxyLayout.AcmeStorageFile]);
        Assert.Equal(ServerFileModes.OwnerReadWrite,
            files.Modes[$"{ProxyLayout.AcmeBackupDirectory}/{result.BackupName}"]);
        Assert.Equal(ServerFileModes.OwnerOnlyDirectory, files.Directories[ProxyLayout.AcmeBackupDirectory]);
        AcmeCertificateEntry remaining = Assert.Single(await store.ListCertificatesAsync(Target,
            TestContext.Current.CancellationToken));
        Assert.Equal("keep.example.com", remaining.MainDomain);
        // The account key and other resolver data are preserved.
        Assert.Contains("\"Account\"", files.Files[ProxyLayout.AcmeStorageFile], StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteCertificatesAsync(Target,
            [new string('0', 64)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Backups_KeepTheNewestTenAndCanBeRestoredOrDeleted()
    {
        var files = new MemoryFileChannel();
        var time = new SteppingTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        var store = new TraefikAcmeStore([files], time);
        var first = AcmeJson(("letsencrypt", "first.example.com", [], DateTimeOffset.UtcNow.AddDays(60)));
        files.Files[ProxyLayout.AcmeStorageFile] = first;
        for (var index = 0; index < 12; index++)
        {
            files.Files[ProxyLayout.AcmeStorageFile] = AcmeJson(("letsencrypt", $"c{index}.example.com", [],
                DateTimeOffset.UtcNow.AddDays(60)));
            await store.RestoreBackupAsync(Target, await SeedBackupAsync(files, store, time, first),
                TestContext.Current.CancellationToken);
        }

        IReadOnlyList<AcmeBackup> backups = await store.ListBackupsAsync(Target, TestContext.Current.CancellationToken);
        Assert.Equal(ITraefikAcmeStore.MaxBackups, backups.Count);
        Assert.True(string.CompareOrdinal(backups[0].Name, backups[^1].Name) > 0);
        Assert.Equal(first, files.Files[ProxyLayout.AcmeStorageFile]);

        await store.DeleteBackupAsync(Target, backups[0].Name, TestContext.Current.CancellationToken);
        Assert.Equal(ITraefikAcmeStore.MaxBackups - 1,
            (await store.ListBackupsAsync(Target, TestContext.Current.CancellationToken)).Count);
    }

    [Theory]
    [InlineData("../acme.json")]
    [InlineData("acme.json")]
    [InlineData("acme-2026.json")]
    [InlineData("acme-20261120T120000000Z.json/../../x")]
    public async Task Backups_RejectNamesOutsideTheBackupPattern(string name)
    {
        var store = new TraefikAcmeStore([new MemoryFileChannel()], TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RestoreBackupAsync(Target, name, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.DeleteBackupAsync(Target, name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestoreBackup_RefusesBackupsThatAreNotValidJson()
    {
        var files = new MemoryFileChannel();
        const string name = "acme-20261120T120000000Z.json";
        files.Files[$"{ProxyLayout.AcmeBackupDirectory}/{name}"] = "not json";
        files.Files[ProxyLayout.AcmeStorageFile] = "{}";
        var store = new TraefikAcmeStore([files], TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RestoreBackupAsync(Target, name, TestContext.Current.CancellationToken));
        Assert.Equal("{}", files.Files[ProxyLayout.AcmeStorageFile]);
    }

    private static async Task<string> SeedBackupAsync(MemoryFileChannel files, TraefikAcmeStore store,
        SteppingTimeProvider time, string contents)
    {
        time.Advance();
        var name = $"acme-{time.GetUtcNow().UtcDateTime:yyyyMMdd'T'HHmmssfff'Z'}.json";
        files.Files[$"{ProxyLayout.AcmeBackupDirectory}/{name}"] = contents;
        time.Advance();
        _ = await store.ListBackupsAsync(Target, TestContext.Current.CancellationToken);
        return name;
    }

    private static string AcmeJson(
        params (string Resolver, string Main, string[] Sans, DateTimeOffset NotAfter)[] certificates)
    {
        var root = new JsonObject();
        foreach (IGrouping<string, (string Resolver, string Main, string[] Sans, DateTimeOffset NotAfter)> resolver in
                 certificates.GroupBy(item => item.Resolver))
        {
            var list = new JsonArray();
            foreach ((string _, string main, string[] sans, DateTimeOffset notAfter) in resolver)
                list.Add(new JsonObject
                {
                    ["domain"] = new JsonObject
                    {
                        ["main"] = main,
                        ["sans"] = new JsonArray(sans.Select(san => (JsonNode)san).ToArray())
                    },
                    ["certificate"] = Convert.ToBase64String(Encoding.ASCII.GetBytes(SelfSignedPem(main, notAfter))),
                    ["key"] = Convert.ToBase64String(Encoding.ASCII.GetBytes(PrivateKeyMarker)),
                    ["Store"] = "default"
                });
            root[resolver.Key] = new JsonObject
            {
                ["Account"] = new JsonObject { ["Email"] = "ops@example.com", ["PrivateKey"] = PrivateKeyMarker },
                ["Certificates"] = list
            };
        }

        return root.ToJsonString();
    }

    private static string SelfSignedPem(string host, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        using X509Certificate2 certificate = request.CreateSelfSigned(notAfter.AddDays(-90), notAfter);
        return certificate.ExportCertificatePem();
    }

    private sealed class SteppingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance()
        {
            _now = _now.AddSeconds(1);
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }
    }

    private sealed class MemoryFileChannel : IServerFileChannel
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, UnixFileMode> Modes { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, UnixFileMode> Directories { get; } = new(StringComparer.Ordinal);

        public bool Supports(ServerConnectionType connectionType)
        {
            return true;
        }

        public string ResolveHostPath(ServerExecutionTarget target, string relativePath)
        {
            return "/var/lib/vessel/" + ServerFilePaths.ValidateRelative(relativePath);
        }

        public Task EnsureDirectoryAsync(ServerExecutionTarget target, string relativePath, UnixFileMode mode,
            CancellationToken cancellationToken = default)
        {
            Directories[ServerFilePaths.ValidateRelative(relativePath)] = mode;
            return Task.CompletedTask;
        }

        public Task<ServerFileWriteResult> WriteAsync(ServerExecutionTarget target, ServerFileWrite file,
            CancellationToken cancellationToken = default)
        {
            var path = ServerFilePaths.ValidateRelative(file.RelativePath);
            Files[path] = file.Contents;
            Modes[path] = file.Mode;
            return Task.FromResult(ServerFileWriteResult.Written);
        }

        public Task<string?> ReadTextAsync(ServerExecutionTarget target, string relativePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Files.GetValueOrDefault(ServerFilePaths.ValidateRelative(relativePath)));
        }

        public Task<IReadOnlyList<string>> ListFilesAsync(ServerExecutionTarget target, string relativeDirectory,
            CancellationToken cancellationToken = default)
        {
            var prefix = ServerFilePaths.ValidateRelative(relativeDirectory) + "/";
            return Task.FromResult<IReadOnlyList<string>>(Files.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(key => key[prefix.Length..])
                .Where(name => !name.Contains('/', StringComparison.Ordinal))
                .ToArray());
        }

        public Task<bool> DeleteFileAsync(ServerExecutionTarget target, string relativePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Files.Remove(ServerFilePaths.ValidateRelative(relativePath)));
        }
    }
}
