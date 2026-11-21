using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Vessel.Application.Docker;
using Vessel.Application.ManagedServices;
using Vessel.Application.Processes;
using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Databases;
using Vessel.Domain.ValueObjects;
using Vessel.Infrastructure.Files;
using Vessel.Infrastructure.ManagedServices;
using Vessel.Infrastructure.Security;

namespace Vessel.IntegrationTests.ManagedServices;

public sealed class ProcessDatabaseBackupProviderTests : IDisposable
{
    private static readonly ContainerRuntimeTarget Docker = new(ContainerRuntimeProvider.Docker);
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), $"vessel-backup-tests-{Guid.NewGuid():N}");
    private readonly List<string> _log = [];

    public void Dispose()
    {
        if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, true);
    }

    [Fact]
    public async Task BackupAsync_StreamsLargePostgresDumpToTempFileWithChecksum()
    {
        var runner = new FakeProcessRunner { DumpBytes = 11 * 1024 * 1024 };
        ProcessDatabaseBackupProvider provider = CreateProvider(runner);
        DatabaseResource database = CreateRunningDatabase(DatabaseEngine.PostgreSql);

        string path;
        await using (BackupArtifactFile artifact = await provider.BackupAsync(
                         new DatabaseBackupRequest(database, new ContainerRuntimeTarget(ContainerRuntimeProvider.Podman),
                             "secret", BackupExecutionId.New()), TestContext.Current.CancellationToken))
        {
            Assert.Equal(runner.DumpBytes, artifact.SizeBytes);
            Assert.Equal("postgresql-custom", artifact.Format);
            Assert.EndsWith(".pgdump", artifact.Key, StringComparison.Ordinal);
            await using Stream content = artifact.OpenRead();
            Assert.Equal(Convert.ToHexString(await SHA256.HashDataAsync(content, TestContext.Current.CancellationToken))
                .ToLowerInvariant(), artifact.Sha256);
            path = ((FileStream)content).Name;
            Assert.True(File.Exists(path));
        }

        Assert.False(File.Exists(path));
        ProcessCommand dump = Assert.Single(runner.StreamingCommands);
        Assert.Equal("podman", dump.FileName);
        Assert.Contains("pg_dump", dump.Arguments);
        Assert.Contains("--format=custom", dump.Arguments);
        Assert.DoesNotContain("secret", dump.Arguments);
        Assert.Equal("secret", dump.Environment?["PGPASSWORD"]);
    }

    [Fact]
    public async Task BackupAsync_GzipsMySqlDumpOfUserDatabasesOnly()
    {
        var runner = new FakeProcessRunner
        {
            DumpText = "-- MySQL dump 10.13\nCREATE TABLE t (id int);\n",
            TextOutput = "information_schema\nmysql\napp\nperformance_schema\nsys\nshop\n"
        };
        ProcessDatabaseBackupProvider provider = CreateProvider(runner);

        await using BackupArtifactFile artifact = await provider.BackupAsync(
            new DatabaseBackupRequest(CreateRunningDatabase(DatabaseEngine.MySql), Docker, "secret",
                BackupExecutionId.New()), TestContext.Current.CancellationToken);

        Assert.Equal("mysql-sql+gzip", artifact.Format);
        Assert.Equal("application/gzip", artifact.ContentType);
        await using Stream content = artifact.OpenRead();
        await using var gzip = new GZipStream(content, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        Assert.Equal(runner.DumpText, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        ProcessCommand dump = Assert.Single(runner.StreamingCommands);
        var databases = dump.Arguments.SkipWhile(argument => argument != "--databases").Skip(1).ToArray();
        Assert.Equal(["app", "shop"], databases);
    }

    [Fact]
    public async Task BackupAsync_RejectsDatabaseNamesThatLookLikeOptions()
    {
        var runner = new FakeProcessRunner { TextOutput = "app\n--result-file=/tmp/x\n" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(runner).BackupAsync(
            new DatabaseBackupRequest(CreateRunningDatabase(DatabaseEngine.MariaDb), Docker, "secret",
                BackupExecutionId.New()), TestContext.Current.CancellationToken));
        Assert.Empty(runner.StreamingCommands);
    }

    [Fact]
    public async Task BackupAsync_DumpsTheSelectedPostgresDatabaseKeepingOwnersInTheArchive()
    {
        var runner = new FakeProcessRunner { DumpBytes = 1024 };

        await using BackupArtifactFile artifact = await CreateProvider(runner).BackupAsync(
            new DatabaseBackupRequest(CreateRunningDatabase(DatabaseEngine.PostgreSql), Docker, "secret",
                BackupExecutionId.New(), "app"), TestContext.Current.CancellationToken);

        ProcessCommand dump = Assert.Single(runner.StreamingCommands);
        Assert.Contains("--dbname=app", dump.Arguments);
        Assert.Contains("--format=custom", dump.Arguments);
        Assert.DoesNotContain("--no-owner", dump.Arguments);
        Assert.Equal("postgresql-custom", artifact.Format);
    }

    [Fact]
    public async Task BackupAsync_PlainSqlFormatIsGzipped()
    {
        var runner = new FakeProcessRunner { DumpText = "-- PostgreSQL database dump\n" };

        await using BackupArtifactFile artifact = await CreateProvider(runner).BackupAsync(
            new DatabaseBackupRequest(CreateRunningDatabase(DatabaseEngine.PostgreSql), Docker, "secret",
                BackupExecutionId.New(), null, BackupDumpFormat.PlainSql), TestContext.Current.CancellationToken);

        Assert.Equal("postgresql-sql+gzip", artifact.Format);
        Assert.EndsWith(".sql.gz", artifact.Key, StringComparison.Ordinal);
        Assert.Contains("--format=plain", Assert.Single(runner.StreamingCommands).Arguments);
    }

    [Fact]
    public async Task BackupAsync_DumpsOnlyTheSelectedMySqlDatabase()
    {
        var runner = new FakeProcessRunner { DumpText = "-- MySQL dump 10.13\n" };

        await using BackupArtifactFile artifact = await CreateProvider(runner).BackupAsync(
            new DatabaseBackupRequest(CreateRunningDatabase(DatabaseEngine.MySql), Docker, "secret",
                BackupExecutionId.New(), "shop"), TestContext.Current.CancellationToken);

        Assert.Empty(runner.TextCommands);
        ProcessCommand dump = Assert.Single(runner.StreamingCommands);
        Assert.Equal(["shop"], dump.Arguments.SkipWhile(argument => argument != "--databases").Skip(1).ToArray());
    }

    [Fact]
    public async Task BackupAsync_RejectsUnsafeSelectedDatabaseNames()
    {
        var runner = new FakeProcessRunner();

        await Assert.ThrowsAsync<Vessel.Domain.Common.DomainException>(() => CreateProvider(runner).BackupAsync(
            new DatabaseBackupRequest(CreateRunningDatabase(DatabaseEngine.PostgreSql), Docker, "secret",
                BackupExecutionId.New(), "--file=/etc/passwd"), TestContext.Current.CancellationToken));
        Assert.Empty(runner.StreamingCommands);
    }

    [Fact]
    public async Task ListDatabasesAsync_ListsConnectablePostgresDatabasesWithThePasswordInTheEnvironment()
    {
        var runner = new FakeProcessRunner { TextOutput = "app\npostgres\n" };

        IReadOnlyList<string> names = await CreateProvider(runner).ListDatabasesAsync(
            new DatabaseListRequest(CreateRunningDatabase(DatabaseEngine.PostgreSql), Docker, "secret"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["app", "postgres"], names);
        ProcessCommand list = Assert.Single(runner.TextCommands);
        Assert.Contains("psql", list.Arguments);
        Assert.DoesNotContain("secret", list.Arguments);
        Assert.Equal("secret", list.Environment?["PGPASSWORD"]);
    }

    [Fact]
    public async Task BackupAsync_PropagatesCancellationAndCleansUp()
    {
        var runner = new FakeProcessRunner { CancelDump = true };
        using var cancellation = new CancellationTokenSource();
        runner.OnDump = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateProvider(runner).BackupAsync(
            new DatabaseBackupRequest(CreateRunningDatabase(DatabaseEngine.PostgreSql), Docker, "secret",
                BackupExecutionId.New()), cancellation.Token));
        Assert.Empty(Directory.Exists(_scratchRoot) ? Directory.GetFileSystemEntries(_scratchRoot) : []);
    }

    [Fact]
    public async Task RestoreAsync_TargetsTheDatabaseTheBackupWasTakenFrom()
    {
        const string dump = "--\n-- PostgreSQL database dump\n--\nCREATE TABLE t (id int);\n";
        var runner = new FakeProcessRunner();

        await CreateProvider(runner).RestoreAsync(
            RestoreRequest(DatabaseEngine.PostgreSql, Encoding.UTF8.GetBytes(dump)) with { DatabaseName = "app" },
            TestContext.Current.CancellationToken);

        Assert.Contains("--dbname=app", Assert.Single(runner.StreamingCommands).Arguments);
    }

    [Fact]
    public async Task RestoreAsync_RestoresPlainPostgresDumpInSingleTransaction()
    {
        const string dump = "--\n-- PostgreSQL database dump\n--\nCREATE TABLE t (id int);\nCOPY public.t (id) FROM stdin;\n1\n\\.\n";
        var runner = new FakeProcessRunner();

        DatabaseRestoreOutcome outcome = await CreateProvider(runner).RestoreAsync(
            RestoreRequest(DatabaseEngine.PostgreSql, Encoding.UTF8.GetBytes(dump)),
            TestContext.Current.CancellationToken);

        Assert.Equal("postgresql-sql", outcome.Format);
        ProcessCommand restore = Assert.Single(runner.StreamingCommands);
        Assert.Contains("psql", restore.Arguments);
        Assert.Contains("--single-transaction", restore.Arguments);
        Assert.Contains("--variable=ON_ERROR_STOP=1", restore.Arguments);
        Assert.Equal(dump, runner.LastStandardInput);
    }

    [Fact]
    public async Task RestoreAsync_DecompressesGzipBeforeRestoring()
    {
        const string dump = "-- MySQL dump 10.13\nUSE `app`;\nINSERT INTO t VALUES ('a\\\\.b');\n";
        var runner = new FakeProcessRunner();

        await CreateProvider(runner).RestoreAsync(RestoreRequest(DatabaseEngine.MySql, Gzip(dump)),
            TestContext.Current.CancellationToken);

        ProcessCommand restore = Assert.Single(runner.StreamingCommands);
        Assert.Contains("--binary-mode", restore.Arguments);
        Assert.Equal(dump, runner.LastStandardInput);
    }

    [Fact]
    public async Task RestoreAsync_BlocksPsqlShellCommandsBeforeTouchingTheTarget()
    {
        const string dump = "-- PostgreSQL database dump\nSELECT 1;\n\\! id\n";
        var runner = new FakeProcessRunner();

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateProvider(runner).RestoreAsync(RestoreRequest(DatabaseEngine.PostgreSql, Encoding.UTF8.GetBytes(dump)),
                TestContext.Current.CancellationToken));

        Assert.Contains("Blocked restore", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.StreamingCommands);
    }

    [Fact]
    public async Task RestoreAsync_BlocksMySqlSystemCommands()
    {
        const string dump = "-- MySQL dump 10.13\nsystem rm -rf /\n";
        var runner = new FakeProcessRunner();

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateProvider(runner).RestoreAsync(
            RestoreRequest(DatabaseEngine.MariaDb, Encoding.UTF8.GetBytes(dump)),
            TestContext.Current.CancellationToken));
        Assert.Empty(runner.StreamingCommands);
    }

    [Fact]
    public async Task RestoreAsync_RejectsChecksumMismatch()
    {
        var runner = new FakeProcessRunner();
        DatabaseRestoreRequest request = RestoreRequest(DatabaseEngine.PostgreSql,
            Encoding.UTF8.GetBytes("-- PostgreSQL database dump\n")) with
        { ExpectedSha256 = new string('0', 64) };

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateProvider(runner).RestoreAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains("checksum", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.StreamingCommands);
    }

    [Fact]
    public async Task RestoreAsync_RejectsArtifactOfAnotherEngine()
    {
        var runner = new FakeProcessRunner();

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateProvider(runner).RestoreAsync(
            RestoreRequest(DatabaseEngine.PostgreSql, Encoding.UTF8.GetBytes("-- MySQL dump 10.13\nSELECT 1;\n")),
            TestContext.Current.CancellationToken));
        Assert.Empty(runner.StreamingCommands);
    }

    [Fact]
    public async Task RestoreAsync_RejectsTruncatedGzip()
    {
        byte[] compressed = Gzip("-- PostgreSQL database dump\n" + new string('x', 50_000));
        var runner = new FakeProcessRunner();

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateProvider(runner).RestoreAsync(
            RestoreRequest(DatabaseEngine.PostgreSql, compressed[..(compressed.Length / 2)]),
            TestContext.Current.CancellationToken));
        Assert.Empty(runner.StreamingCommands);
    }

    [Fact]
    public async Task RestoreAsync_DryRunInspectsArchiveWithoutRestoring()
    {
        var runner = new FakeProcessRunner { DumpText = "CREATE TABLE t (id int);\n" };
        byte[] archive = [.. "PGDMP"u8, 1, 14, 0, 4, 8];

        DatabaseRestoreOutcome outcome = await CreateProvider(runner).RestoreAsync(
            RestoreRequest(DatabaseEngine.PostgreSql, archive) with
            {
                Options = new DatabaseRestoreOptions(true, ReplaceExisting: true)
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("postgresql-archive", outcome.Format);
        ProcessCommand inspect = Assert.Single(runner.StreamingCommands);
        Assert.Equal("pg_restore", inspect.Arguments[^1]);
        Assert.DoesNotContain("--dbname=postgres", inspect.Arguments);
        Assert.Contains("Dry run", outcome.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreAsync_BlocksProgramExecutionInsideArchives()
    {
        var runner = new FakeProcessRunner { DumpText = "COPY t TO PROGRAM 'id';\n" };
        byte[] archive = [.. "PGDMP"u8, 1, 14, 0];

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateProvider(runner).RestoreAsync(
            RestoreRequest(DatabaseEngine.PostgreSql, archive), TestContext.Current.CancellationToken));
        Assert.Single(runner.StreamingCommands);
    }

    [Fact]
    public async Task RestoreAsync_RestoresArchiveWithPgRestoreOptions()
    {
        var runner = new FakeProcessRunner { DumpText = "CREATE TABLE t (id int);\n" };
        byte[] archive = [.. "PGDMP"u8, 1, 14, 0];

        await CreateProvider(runner).RestoreAsync(
            RestoreRequest(DatabaseEngine.PostgreSql, archive) with
            {
                Options = new DatabaseRestoreOptions(false, ReplaceExisting: true)
            },
            TestContext.Current.CancellationToken);

        ProcessCommand restore = runner.StreamingCommands[^1];
        Assert.Contains("--single-transaction", restore.Arguments);
        Assert.Contains("--exit-on-error", restore.Arguments);
        Assert.Contains("--clean", restore.Arguments);
        Assert.Contains("--no-owner", restore.Arguments);
    }

    [Fact]
    public async Task RestoreAsync_ReplacesRedisDumpFileInsteadOfPipingIt()
    {
        var runner = new FakeProcessRunner();
        byte[] rdb = [.. "REDIS0011"u8, 0xFA, 0xFF];

        await CreateProvider(runner).RestoreAsync(RestoreRequest(DatabaseEngine.Redis, Gzip(rdb)),
            TestContext.Current.CancellationToken);

        Assert.Empty(runner.StreamingCommands);
        string[] runtimeVerbs = runner.TextCommands.Where(command => command.Arguments[0] != "exec")
            .Select(command => command.Arguments[0]).ToArray();
        Assert.Equal(["stop", "cp", "start"], runtimeVerbs);
        Assert.DoesNotContain(runner.TextCommands, command => command.Arguments.Contains("--pipe"));
        ProcessCommand copy = runner.TextCommands.Single(command => command.Arguments[0] == "cp");
        Assert.Equal("vessel-db:/data/dump.rdb", copy.Arguments[2]);
        Assert.Equal(rdb, runner.CopiedFile);
    }

    [Fact]
    public async Task RestoreAsync_RefusesRedisWithAppendOnlyPersistence()
    {
        var runner = new FakeProcessRunner { RedisAppendOnly = "yes" };
        byte[] rdb = [.. "REDIS0011"u8, 0xFF];

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(runner).RestoreAsync(
            RestoreRequest(DatabaseEngine.Redis, rdb), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(runner.TextCommands, command => command.Arguments[0] == "stop");
    }

    private ProcessDatabaseBackupProvider CreateProvider(FakeProcessRunner runner)
    {
        return new ProcessDatabaseBackupProvider(runner, new SecretRedactor(), new PathSafetyService(),
            TimeProvider.System, _scratchRoot);
    }

    private DatabaseRestoreRequest RestoreRequest(DatabaseEngine engine, byte[] artifact)
    {
        return new DatabaseRestoreRequest(
            CreateRunningDatabase(engine),
            Docker,
            "secret",
            new MemoryStream(artifact),
            Convert.ToHexString(SHA256.HashData(artifact)).ToLowerInvariant(),
            engine,
            new DatabaseRestoreOptions(false),
            (line, _) =>
            {
                _log.Add(line);
                return Task.CompletedTask;
            });
    }

    private static byte[] Gzip(string text)
    {
        return Gzip(Encoding.UTF8.GetBytes(text));
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
        {
            gzip.Write(bytes);
        }

        return output.ToArray();
    }

    private static DatabaseResource CreateRunningDatabase(DatabaseEngine engine)
    {
        DateTimeOffset now = new(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        var database = DatabaseResource.Create(
            EnvironmentId.New(),
            ServerId.New(),
            new ResourceName("database"),
            engine,
            new VersionLabel("latest"),
            new StorageConfiguration("db-data", "/data"),
            SecretReferenceId.New(),
            now);
        database.MarkProvisioning(now);
        database.MarkRunning("vessel-db", "docker-compose.yml", now.AddSeconds(1));
        return database;
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public long DumpBytes { get; init; }

        public string? DumpText { get; init; }

        public string TextOutput { get; init; } = string.Empty;

        public string RedisAppendOnly { get; init; } = "no";

        public bool CancelDump { get; init; }

        public Action? OnDump { get; set; }

        public List<ProcessCommand> StreamingCommands { get; } = [];

        public List<ProcessCommand> TextCommands { get; } = [];

        public string? LastStandardInput { get; private set; }

        public byte[]? CopiedFile { get; private set; }

        public async Task<ProcessResult> RunStreamingAsync(ProcessCommand command, ProcessStreams streams,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(ProcessOutputMode.Stream, command.OutputMode);
            StreamingCommands.Add(command);
            if (streams.StandardInput is not null)
            {
                using var reader = new StreamReader(streams.StandardInput, leaveOpen: true);
                LastStandardInput = await reader.ReadToEndAsync(cancellationToken);
            }

            if (CancelDump)
            {
                OnDump?.Invoke();
                return Result(command, new ProcessExitInfo(-1, true, false, null));
            }

            if (streams.StandardOutput is not null)
            {
                if (DumpText is not null)
                    await streams.StandardOutput.WriteAsync(Encoding.UTF8.GetBytes(DumpText), cancellationToken);
                var buffer = new byte[64 * 1024];
                for (long written = 0; written < DumpBytes; written += buffer.Length)
                    await streams.StandardOutput.WriteAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, DumpBytes - written)), cancellationToken);
            }

            return Result(command, new ProcessExitInfo(0, false, false, null));
        }

        public Task<ProcessResult> RunTextAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            TextCommands.Add(command);
            var output = TextOutput;
            if (command.Arguments.Contains("CONFIG"))
            {
                var parameter = command.Arguments[^1];
                output = parameter switch
                {
                    "appendonly" => $"appendonly\n{RedisAppendOnly}\n",
                    "dir" => "dir\n/data\n",
                    _ => "dbfilename\ndump.rdb\n"
                };
            }

            if (command.Arguments[0] == "cp") CopiedFile = File.ReadAllBytes(command.Arguments[1]);
            return Task.FromResult(Result(command, new ProcessExitInfo(0, false, false, null)) with
            {
                StandardOutput = output
            });
        }

        public Task<ProcessExitInfo> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ProcessBinaryResult> RunBinaryAsync(ProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Backups must not buffer dumps in memory.");
        }

        public Task<ProcessResult> RunTextWithInputAsync(ProcessCommand command, Stream standardInput,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<ProcessOutputLine> StreamLinesAsync(ProcessCommand command,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IInteractiveProcess> StartInteractiveAsync(ProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        private static ProcessResult Result(ProcessCommand command, ProcessExitInfo exit)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new ProcessResult(command, exit, 123, now, now, string.Empty, string.Empty);
        }
    }
}
