using System.Text;
using Vessel.Application.Backups;
using Vessel.Application.Backups.Volumes;
using Vessel.Application.ManagedServices;
using Vessel.Application.ManagedServices.DatabaseImports;
using Vessel.Application.Security;
using Vessel.Application.Servers;
using Vessel.Application.Servers.Files;
using Vessel.Application.Storage.Persistent;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Common;
using Vessel.Domain.Servers;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;
using Vessel.UnitTests.Application.SecurityHardening;
using AppEntity = Vessel.Domain.Applications.Application;

namespace Vessel.UnitTests.Application.Backups;

/// <summary>Backup fixture plus an application, a team admin who is not an instance admin, and storage doubles.</summary>
internal sealed class PersistentStorageKit
{
    public PersistentStorageKit()
    {
        F = new BackupFixture();
        Admin = User.Create(new DisplayName("admin"), new EmailAddress("admin@example.com"), F.Now);
        F.Team.AddMember(Admin.Id, TeamRole.Admin, F.Now);
        F.Db.UserItems.Add(Admin);
        Application = AppEntity.Create(F.Database.EnvironmentId, F.Server.Id, new ResourceName("web"),
            new GitSource(new RepositoryUrl("https://github.com/example/web.git"), "main"),
            BuildConfiguration.Default(ApplicationBuildPack.Dockerfile), F.Now);
        F.Db.ApplicationItems.Add(Application);
    }

    public BackupFixture F { get; }

    public User Admin { get; }

    public AppEntity Application { get; }

    public FakeCipher Cipher { get; } = new();

    public FakeFileChannel Channel { get; } = new();

    public FakeArchiveRunner Runner { get; } = new();

    public FakeImportStaging Staging { get; } = new();

    public StorageOwnerAccess Owners => new(F.Db, F.Authorization);

    public ResourceStorageService Storage()
    {
        return new ResourceStorageService(F.Db, Owners, F.Authorization, Cipher, F.Audit, F.Time);
    }

    public ResourceStorageProvisioner Provisioner()
    {
        return new ResourceStorageProvisioner(F.Db, [Channel], Cipher, F.Time);
    }

    public VolumeBackupService VolumeBackups()
    {
        return new VolumeBackupService(F.Db, F.Authorization, Owners, F.Jobs, F.Jobs, F.Locks,
            new PassthroughRedactor(), Runner, F.Storage, F.Connector, [Channel], F.Audit, F.Notifications, F.Time);
    }

    public DatabaseImportService Imports()
    {
        return new DatabaseImportService(F.Db, F.Authorization, Staging, F.Locks, F.Jobs, F.Audit, F.Time);
    }

    public DatabaseImportArtifactSource ImportSource()
    {
        return new DatabaseImportArtifactSource(F.Db, Staging, new BackupArtifactReader(F.Db, F.Storage, F.Connector),
            F.Authorization, F.Time);
    }
}

internal sealed class FakeCipher : ISecretCipher
{
    public string ActiveKeyVersion => "v1";

    public SecretCipherText Encrypt(string plaintext, string associatedData) => throw new NotSupportedException();

    public string Decrypt(SecretCipherText cipherText, string associatedData) => throw new NotSupportedException();

    public string Protect(string plaintext, string purpose) =>
        $"enc:{purpose}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext))}";

    public string Unprotect(string protectedValue, string purpose)
    {
        var prefix = $"enc:{purpose}:";
        if (!protectedValue.StartsWith(prefix, StringComparison.Ordinal))
            throw new SecretDecryptionException();
        return Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[prefix.Length..]));
    }

    public bool IsProtected(string value) => value.StartsWith("enc:", StringComparison.Ordinal);

    public string? GetProtectedKeyVersion(string value) => IsProtected(value) ? "v1" : null;
}

internal sealed class FakeFileChannel : IServerFileChannel
{
    public Dictionary<string, string> Files { get; } = [];

    public HashSet<string> Directories { get; } = [];

    public bool Supports(ServerConnectionType connectionType) => true;

    public string ResolveHostPath(ServerExecutionTarget target, string relativePath) =>
        "/var/lib/vessel/" + ServerFilePaths.ValidateRelative(relativePath);

    public Task EnsureDirectoryAsync(ServerExecutionTarget target, string relativePath, UnixFileMode mode,
        CancellationToken cancellationToken = default)
    {
        Directories.Add(ServerFilePaths.ValidateRelative(relativePath));
        return Task.CompletedTask;
    }

    public Task<ServerFileWriteResult> WriteAsync(ServerExecutionTarget target, ServerFileWrite file,
        CancellationToken cancellationToken = default)
    {
        var path = ServerFilePaths.ValidateRelative(file.RelativePath);
        if (file.CreateOnly && Files.ContainsKey(path)) return Task.FromResult(ServerFileWriteResult.AlreadyExists);
        Files[path] = file.Contents;
        return Task.FromResult(ServerFileWriteResult.Written);
    }

    public Task<string?> ReadTextAsync(ServerExecutionTarget target, string relativePath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.GetValueOrDefault(relativePath));
}

internal sealed class FakeArchiveRunner : IVolumeArchiveRunner
{
    public List<string> Containers { get; set; } = [];

    public List<string> Calls { get; } = [];

    public bool FailStart { get; set; }

    public bool Exists { get; set; } = true;

    public VolumeArchiveSource? LastSource { get; private set; }

    public byte[]? RestoredBytes { get; private set; }

    public Task<bool> SourceExistsAsync(ServerExecutionTarget server, VolumeArchiveSource source,
        CancellationToken cancellationToken = default)
    {
        LastSource = source;
        return Task.FromResult(Exists);
    }

    public Task<IReadOnlyList<string>> ListContainersUsingAsync(ServerExecutionTarget server,
        VolumeArchiveSource source, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Containers);

    public Task StopContainersAsync(ServerExecutionTarget server, IReadOnlyList<string> containerIds,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("stop:" + string.Join(',', containerIds));
        return Task.CompletedTask;
    }

    public Task StartContainersAsync(ServerExecutionTarget server, IReadOnlyList<string> containerIds,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("start:" + string.Join(',', containerIds));
        return FailStart ? throw new InvalidOperationException("ssh: connection refused") : Task.CompletedTask;
    }

    public Task<VolumeArchive> ArchiveAsync(ServerExecutionTarget server, VolumeArchiveSource source,
        Guid operationId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Calls.Add("archive");
        var bytes = Encoding.UTF8.GetBytes("tarball");
        return Task.FromResult(new VolumeArchive(bytes.Length, "cafe", null, () => new MemoryStream(bytes),
            () => ValueTask.CompletedTask));
    }

    public async Task RestoreAsync(ServerExecutionTarget server, VolumeArchiveSource source, Stream archive,
        string? expectedSha256, bool replaceExisting, Guid operationId, TimeSpan timeout,
        Func<string, CancellationToken, Task> log, CancellationToken cancellationToken = default)
    {
        Calls.Add("restore");
        using var buffer = new MemoryStream();
        await archive.CopyToAsync(buffer, cancellationToken);
        RestoredBytes = buffer.ToArray();
        await log("restored", cancellationToken);
    }
}

internal sealed class FakeImportStaging : IDatabaseImportStaging
{
    public Dictionary<Guid, MemoryStream> Uploads { get; } = [];

    public int PathValidations { get; private set; }

    public long MaxImportBytes => 1024;

    public async Task<long> AppendAsync(DatabaseImportUploadId uploadId, long offset, Stream content, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (!Uploads.TryGetValue(uploadId.Value, out MemoryStream? stream))
            Uploads[uploadId.Value] = stream = new MemoryStream();
        stream.SetLength(offset);
        stream.Position = offset;
        using var chunk = new MemoryStream();
        await content.CopyToAsync(chunk, cancellationToken);
        if (chunk.Length > maxBytes) throw new DomainException("too large");
        chunk.Position = 0;
        await chunk.CopyToAsync(stream, cancellationToken);
        return chunk.Length;
    }

    public Task<Stream> OpenReadAsync(DatabaseImportUploadId uploadId, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream(Uploads[uploadId.Value].ToArray()));

    public Task DeleteAsync(DatabaseImportUploadId uploadId, CancellationToken cancellationToken = default)
    {
        Uploads.Remove(uploadId.Value);
        return Task.CompletedTask;
    }

    public IReadOnlyList<Guid> ListStaged() => Uploads.Keys.ToArray();

    public string ValidateServerPath(string path)
    {
        PathValidations++;
        return path.StartsWith("/imports/", StringComparison.Ordinal)
            ? path
            : throw new DomainException("outside the allowed import directories");
    }

    public Task<Stream> OpenServerFileAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("server dump")));
}

internal sealed class RecordingRestoreProvider : IDatabaseBackupProvider
{
    public List<DatabaseRestoreRequest> Restores { get; } = [];

    public string? LastArtifact { get; private set; }

    public Task<BackupArtifactFile> BackupAsync(DatabaseBackupRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async Task<DatabaseRestoreOutcome> RestoreAsync(DatabaseRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        Restores.Add(request);
        using var reader = new StreamReader(request.Artifact);
        LastArtifact = await reader.ReadToEndAsync(cancellationToken);
        return new DatabaseRestoreOutcome("postgresql-sql", "Restore completed.");
    }
}
