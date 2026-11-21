using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using Vessel.Application.Backups.Volumes;
using Vessel.Application.Common;
using Vessel.Application.Deployments;
using Vessel.Application.Processes;
using Vessel.Application.Servers;
using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Domain.Servers;
using Vessel.Domain.Storage;
using Vessel.Infrastructure.Backups.Volumes;
using Vessel.Infrastructure.Deployments;
using Vessel.Infrastructure.Files;
using Vessel.Infrastructure.ManagedServices.DatabaseImports;
using Vessel.Infrastructure.Persistence;
using YamlDotNet.RepresentationModel;

namespace Vessel.IntegrationTests.Storage;

/// <summary>DATA-12 compose mounts, DATA-09 helper container commands and DATA-10 import staging.</summary>
public sealed class PersistentStorageInfrastructureTests
{
    private static readonly ServerExecutionTarget Local = new(ServerId.New(), ServerConnectionType.Local,
        "localhost", 22, null, ContainerRuntimeKind.Docker);

    private readonly YamlComposeDocumentBuilder _builder = new(new PathSafetyService());

    [Fact]
    public void AddStorageMounts_AddsNamedVolumesWithFixedNamesAndNonCreatingBinds()
    {
        const string yaml = """
                            services:
                              web:
                                image: nginx
                              worker:
                                image: busybox
                                volumes:
                                  - cache:/cache
                            volumes:
                              cache: {}
                            """;
        var owner = Guid.NewGuid();
        var volume = StoragePaths.RuntimeVolumeName(owner, "data");
        var result = _builder.AddStorageMounts(yaml, new ComposeStorageSpec("web",
        [
            new ComposeStorageMount(ComposeStorageMountKind.Volume, null, volume, "/data"),
            new ComposeStorageMount(ComposeStorageMountKind.Bind, "worker", "/var/lib/vessel/resources/x/app.conf",
                "/etc/app.conf")
        ]));
        var root = Load(result);
        var top = (YamlMappingNode)root["volumes"];
        Assert.Equal(volume, ((YamlScalarNode)((YamlMappingNode)top[volume])["name"]).Value);
        Assert.True(top.Children.ContainsKey(new YamlScalarNode("cache")));
        var worker = (YamlSequenceNode)((YamlMappingNode)((YamlMappingNode)root["services"])["worker"])["volumes"];
        var bind = Assert.IsType<YamlMappingNode>(worker.Children[1]);
        Assert.Equal("bind", ((YamlScalarNode)bind["type"]).Value);
        Assert.Equal("false", ((YamlScalarNode)((YamlMappingNode)bind["bind"])["create_host_path"]).Value);
    }

    [Fact]
    public void AddStorageMounts_RejectsUnknownServicesDuplicateTargetsAndAmbiguousDefaults()
    {
        const string yaml = """
                            services:
                              web:
                                image: nginx
                                volumes:
                                  - ./data:/data
                              db:
                                image: postgres
                            """;
        Assert.Throws<ComposeValidationException>(() => _builder.AddStorageMounts(yaml, new ComposeStorageSpec("web",
            [new ComposeStorageMount(ComposeStorageMountKind.Volume, null, "vessel-v", "/data/")])));
        Assert.Throws<ComposeValidationException>(() => _builder.AddStorageMounts(yaml, new ComposeStorageSpec("web",
            [new ComposeStorageMount(ComposeStorageMountKind.Volume, "missing", "vessel-v", "/x")])));
        Assert.Throws<ComposeValidationException>(() => _builder.AddStorageMounts(yaml, new ComposeStorageSpec(null,
            [new ComposeStorageMount(ComposeStorageMountKind.Volume, null, "vessel-v", "/x")])));
    }

    [Theory]
    [InlineData(VolumeBackupTargetKind.PersistentVolume, "--privileged")]
    [InlineData(VolumeBackupTargetKind.PersistentVolume, "my-volume")]
    [InlineData(VolumeBackupTargetKind.DirectoryMount, "/var/lib/../../etc")]
    [InlineData(VolumeBackupTargetKind.DirectoryMount, "/data:/etc")]
    [InlineData(VolumeBackupTargetKind.DirectoryMount, "relative/path")]
    public void ArchiveRunner_RejectsSourcesThatAreNotVesselManaged(VolumeBackupTargetKind kind, string source)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ContainerVolumeArchiveRunner.Validate(new VolumeArchiveSource(kind, source)));
    }

    [Fact]
    public void ArchiveRunner_BackupHelperIsPinnedIsolatedAndReadOnly()
    {
        var volume = StoragePaths.RuntimeVolumeName(Guid.NewGuid(), "data", "pr-2");
        IReadOnlyList<string> args = ContainerVolumeArchiveRunner.ArchiveArguments(Local,
            new VolumeArchiveSource(VolumeBackupTargetKind.PersistentVolume, volume), Guid.NewGuid());
        Assert.Equal("docker", args[0]);
        Assert.Contains($"{volume}:/volume:ro", args);
        Assert.Contains("none", args);
        Assert.Contains("no-new-privileges", args);
        Assert.Contains(ComponentVersions.VolumeBackupHelperImage, args);
        IReadOnlyList<string> restore = ContainerVolumeArchiveRunner.RestoreArguments(
            Local with { Runtime = ContainerRuntimeKind.Podman },
            new VolumeArchiveSource(VolumeBackupTargetKind.DirectoryMount, "/var/lib/vessel/resources/a/data"),
            Guid.NewGuid(), true);
        Assert.Equal("podman", restore[0]);
        Assert.Equal("replace", restore[^1]);
    }

    [Fact]
    public void ArchiveRunner_FindsContainersByMountNameOrSource()
    {
        const string output = "aaaaaaaaaaaa\tvessel-x\t/var/lib/docker/volumes/vessel-x/_data\n" +
                              "bbbbbbbbbbbb\t\t/var/lib/vessel/resources/a/data\n" +
                              "cccccccccccc\tother\t/other\n";
        Assert.Equal(["aaaaaaaaaaaa"], ContainerVolumeArchiveRunner.ParseMountingContainers(output, "vessel-x"));
        Assert.Equal(["bbbbbbbbbbbb"],
            ContainerVolumeArchiveRunner.ParseMountingContainers(output, "/var/lib/vessel/resources/a/data"));
    }

    [Fact]
    public async Task ArchiveRunner_StreamsAndHashes_ThenRestoreVerifiesTheChecksum()
    {
        var scratch = Directory.CreateTempSubdirectory("vessel-volume-test").FullName;
        try
        {
            var commands = new StreamingCommands();
            var runner = new ContainerVolumeArchiveRunner(commands, new PathSafetyService(), scratch);
            var source = new VolumeArchiveSource(VolumeBackupTargetKind.PersistentVolume,
                StoragePaths.RuntimeVolumeName(Guid.NewGuid(), "data"));
            await using VolumeArchive archive = await runner.ArchiveAsync(Local, source, Guid.NewGuid(),
                TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
            Assert.Equal(commands.Payload.Length, archive.SizeBytes);
            Assert.NotNull(archive.Warning);

            await Assert.ThrowsAsync<InvalidDataException>(() => runner.RestoreAsync(Local, source,
                new MemoryStream(commands.Payload), "00", true, Guid.NewGuid(), TimeSpan.FromMinutes(1),
                (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken));
            Assert.Null(commands.RestoredInput);
            await runner.RestoreAsync(Local, source, archive.OpenRead(), archive.Sha256, true, Guid.NewGuid(),
                TimeSpan.FromMinutes(1), (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);
            Assert.Equal(commands.Payload, commands.RestoredInput);
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }

    [Fact]
    public async Task ImportStaging_BoundsChunksAndConfinesServerPaths()
    {
        var root = Directory.CreateTempSubdirectory("vessel-import-test").FullName;
        try
        {
            var allowed = Directory.CreateDirectory(Path.Combine(root, "allowed")).FullName;
            var dump = Path.Combine(allowed, "dump.sql");
            await File.WriteAllTextAsync(dump, "SELECT 1;", TestContext.Current.CancellationToken);
            var outside = Path.Combine(root, "secret.sql");
            await File.WriteAllTextAsync(outside, "x", TestContext.Current.CancellationToken);
            var staging = new FileDatabaseImportStaging(Options.Create(new DatabaseImportOptions
            {
                StagingDirectory = Path.Combine(root, "staging"),
                ServerPathRoots = [allowed]
            }), new PathSafetyService());

            var id = DatabaseImportUploadId.New();
            Assert.Equal(3, await staging.AppendAsync(id, 0, new MemoryStream([1, 2, 3]), 10,
                TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<DomainException>(() => staging.AppendAsync(id, 3, new MemoryStream(new byte[20]),
                10, TestContext.Current.CancellationToken));
            await using (Stream read = await staging.OpenReadAsync(id, TestContext.Current.CancellationToken))
            {
                Assert.Equal(3, read.Length);
            }

            Assert.Contains(id.Value, staging.ListStaged());
            await staging.DeleteAsync(id, TestContext.Current.CancellationToken);
            Assert.Empty(staging.ListStaged());

            Assert.Equal(dump, staging.ValidateServerPath(dump));
            Assert.Throws<DomainException>(() => staging.ValidateServerPath(outside));
            Assert.Throws<DomainException>(() => staging.ValidateServerPath(Path.Combine(allowed, "..", "secret.sql")));
            Assert.Throws<DomainException>(() => staging.ValidateServerPath("dump.sql"));
            if (!OperatingSystem.IsWindows())
            {
                var link = Path.Combine(allowed, "link.sql");
                File.CreateSymbolicLink(link, outside);
                Assert.Throws<DomainException>(() => staging.ValidateServerPath(link));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Model_MapsStorageVolumeBackupsAndImports()
    {
        DbContextOptions<VesselDbContext> options = new DbContextOptionsBuilder<VesselDbContext>()
            .UseNpgsql("Host=localhost;Database=vessel_model_test;Username=vessel;Password=vessel").Options;
        using var context = new VesselDbContext(options);
        Assert.Equal("persistent_volumes", context.Model.FindEntityType(typeof(PersistentVolume))?.GetTableName());
        Assert.Equal("file_mounts", context.Model.FindEntityType(typeof(FileMount))?.GetTableName());
        IEntityType schedule = context.Model.FindEntityType(typeof(ScheduledVolumeBackup))!;
        Assert.Contains(schedule.GetIndexes(), index => index.IsUnique && index.Properties.Count == 2);
        Assert.Equal("volume_backup_replicas",
            context.Model.FindEntityType(typeof(VolumeBackupReplica))?.GetTableName());
        Assert.Equal("database_import_uploads",
            context.Model.FindEntityType(typeof(DatabaseImportUpload))?.GetTableName());
        IEntityType restore = context.Model.FindEntityType(typeof(DatabaseRestore))!;
        Assert.True(restore.FindProperty(nameof(DatabaseRestore.BackupExecutionId))!.IsNullable);
        Assert.Null(restore.FindProperty(nameof(DatabaseRestore.IsImport)));
    }

    private static YamlMappingNode Load(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    /// <summary>Plays the runtime CLI: emits a gzip "archive" with a tar warning, records restore input.</summary>
    private sealed class StreamingCommands : IServerCommandRunner
    {
        public byte[] Payload { get; } = Gzip("volume content");

        public byte[]? RestoredInput { get; private set; }

        public Task<ServerCommandResult> RunAsync(ServerExecutionTarget target, IReadOnlyList<string> arguments,
            TimeSpan timeout, string? standardInput = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServerCommandResult(0, string.Empty, string.Empty));

        public Task<ServerCommandResult> RunWithKeyAsync(ServerExecutionTarget target, SshKeyId keyId,
            IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<ServerCommandResult> RunStreamingAsync(ServerExecutionTarget target,
            IReadOnlyList<string> arguments, TimeSpan timeout, ProcessStreams streams,
            CancellationToken cancellationToken = default)
        {
            if (streams.StandardOutput is not null)
            {
                await streams.StandardOutput.WriteAsync(Payload, cancellationToken);
                return new ServerCommandResult(0, string.Empty,
                    "vessel-warning: files changed while they were archived");
            }

            using var buffer = new MemoryStream();
            await streams.StandardInput!.CopyToAsync(buffer, cancellationToken);
            RestoredInput = buffer.ToArray();
            return new ServerCommandResult(0, string.Empty, string.Empty);
        }

        public IAsyncEnumerable<ProcessOutputLine> StreamAsync(ServerExecutionTarget target,
            IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout,
            IReadOnlyList<string>? secretValues = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static byte[] Gzip(string text)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
            {
                gzip.Write(Encoding.UTF8.GetBytes(text));
            }

            return output.ToArray();
        }
    }
}
