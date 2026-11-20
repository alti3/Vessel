using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;
using System.Text.RegularExpressions;
using Vessel.Application.Files;
using Vessel.Application.ManagedServices;
using Vessel.Application.Processes;
using Vessel.Application.Security;
using Vessel.Domain.Databases;
using Vessel.Infrastructure.ManagedServices.Backups;

namespace Vessel.Infrastructure.ManagedServices;

/// <summary>
/// Backs up and restores managed databases by running the engine's own client tools inside the database container
/// through the configured container runtime CLI (Docker or Podman). Dumps stream from the process into an owned
/// temporary file (gzip-compressed unless the format is already compressed, SHA-256 computed while streaming) and
/// are never buffered in memory. Restores verify the checksum, decompress and detect the format, check engine
/// compatibility and scan for code-execution constructs before anything in the target is changed.
/// </summary>
/// <remarks>
/// The runtime CLI talks to the local Docker/Podman socket, which is host-root-equivalent; see
/// <c>docs/deployment/phase-11-managed-services-backups-restore.md</c>.
/// </remarks>
public sealed partial class ProcessDatabaseBackupProvider : IDatabaseBackupProvider
{
    public const string ArtifactBucket = "vessel-backups";

    /// <summary>Hard upper bound for any single tool invocation; callers pass a tighter deadline token.</summary>
    private static readonly TimeSpan MaxProcessDuration = TimeSpan.FromHours(24);

    private static readonly TimeSpan ControlCommandTimeout = TimeSpan.FromMinutes(2);

    private const long DiagnosticOutputLimit = 1024 * 1024;

    private const int LogTailLength = 2000;

    private readonly IProcessRunner _processes;
    private readonly ISecretRedactor _redactor;
    private readonly IPathSafetyService _paths;
    private readonly TimeProvider _timeProvider;
    private readonly string _scratchRoot;

    public ProcessDatabaseBackupProvider(
        IProcessRunner processes,
        ISecretRedactor redactor,
        IPathSafetyService paths,
        TimeProvider timeProvider)
        : this(processes, redactor, paths, timeProvider,
            Path.Combine(AppContext.BaseDirectory, "storage", "backup-scratch"))
    {
    }

    public ProcessDatabaseBackupProvider(
        IProcessRunner processes,
        ISecretRedactor redactor,
        IPathSafetyService paths,
        TimeProvider timeProvider,
        string scratchRoot)
    {
        _processes = processes;
        _redactor = redactor;
        _paths = paths;
        _timeProvider = timeProvider;
        _scratchRoot = Path.GetFullPath(scratchRoot);
    }

    public async Task<BackupArtifactFile> BackupAsync(
        DatabaseBackupRequest request,
        CancellationToken cancellationToken = default)
    {
        DatabaseResource database = request.Database;
        var container = RequireContainerName(database);
        var cli = request.Runtime.ResolveCliExecutable();
        BackupPlan plan = await CreateBackupPlanAsync(cli, container, database, request.Credentials,
            cancellationToken);
        BackupScratchDirectory scratch =
            BackupScratchDirectory.Create(_scratchRoot, "backup", request.ExecutionId.Value, _paths);
        try
        {
            var fileName = "artifact" + plan.Extension;
            long size;
            string sha256;
            await using (FileStream file = scratch.CreateFile(fileName))
            await using (var hashing = new HashingWriteStream(file, true))
            {
                GZipStream? gzip = plan.Gzip ? new GZipStream(hashing, CompressionLevel.Fastest, true) : null;
                try
                {
                    Stream sink = (Stream?)gzip ?? hashing;
                    if (plan.Arguments is null)
                    {
                        await sink.WriteAsync(Encoding.UTF8.GetBytes(MySqlRestoreScriptFilter.EmptyDumpHeader + "\n"),
                            cancellationToken);
                    }
                    else
                    {
                        ProcessResult result = await _processes.RunStreamingAsync(
                            Command(cli, plan.Arguments, database, request.Credentials, MaxProcessDuration),
                            new ProcessStreams(StandardOutput: sink), cancellationToken);
                        EnsureSucceeded(result, plan.Tool, cancellationToken);
                    }
                }
                finally
                {
                    if (gzip is not null) await gzip.DisposeAsync();
                }

                await hashing.FlushAsync(cancellationToken);
                size = hashing.BytesWritten;
                sha256 = hashing.GetSha256Hex();
            }

            if (size == 0) throw new InvalidOperationException($"{plan.Tool} produced an empty backup.");
            var key =
                $"database/{database.Id.Value:D}/{_timeProvider.GetUtcNow():yyyyMMddHHmmss}-{request.ExecutionId.Value:N}{plan.Extension}";
            return new BackupArtifactFile(ArtifactBucket, key, size, sha256, plan.ContentType, plan.Format,
                () => scratch.OpenRead(fileName), scratch.DisposeAsync);
        }
        catch
        {
            await scratch.DisposeAsync();
            throw;
        }
    }

    public async Task<DatabaseRestoreOutcome> RestoreAsync(
        DatabaseRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        DatabaseResource target = request.Target;
        DatabaseRestoreOptions options = request.Options;
        var container = RequireContainerName(target);
        var cli = request.Runtime.ResolveCliExecutable();
        await using BackupScratchDirectory scratch =
            BackupScratchDirectory.Create(_scratchRoot, "restore", target.Id.Value, _paths);

        // 1. Download to owned storage, hashing while streaming, and verify the recorded checksum.
        await request.Log("Downloading the backup artifact.", cancellationToken);
        long downloaded;
        string sha256;
        await using (FileStream file = scratch.CreateFile("artifact"))
        await using (var hashing = new HashingWriteStream(file, true))
        {
            await request.Artifact.CopyToAsync(hashing, cancellationToken);
            await hashing.FlushAsync(cancellationToken);
            downloaded = hashing.BytesWritten;
            sha256 = hashing.GetSha256Hex();
        }

        await request.Log($"Downloaded {downloaded} bytes.", cancellationToken);
        if (string.IsNullOrWhiteSpace(request.ExpectedSha256))
            await request.Log("No checksum was recorded for this backup; checksum verification skipped.",
                cancellationToken);
        else if (!string.Equals(request.ExpectedSha256, sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The artifact checksum does not match the checksum recorded when the backup was taken; it may be " +
                "corrupt or tampered with. Nothing was changed.");
        else
            await request.Log("SHA-256 checksum verified.", cancellationToken);

        // 2. Normalize: decompress gzip artifacts and verify they are complete.
        var payload = "artifact";
        var header = await ReadHeaderAsync(scratch, payload, cancellationToken);
        var gzip = BackupArtifactInspector.IsGzip(header);
        if (gzip)
        {
            payload = "payload";
            long decompressed;
            await using (FileStream compressed = scratch.OpenRead("artifact"))
            await using (FileStream output = scratch.CreateFile(payload))
            {
                decompressed = await BackupArtifactInspector.DecompressGzipAsync(compressed, output,
                    cancellationToken);
            }

            await request.Log($"Gzip integrity verified ({decompressed} bytes decompressed).", cancellationToken);
            header = await ReadHeaderAsync(scratch, payload, cancellationToken);
        }

        // 3. Detect the payload format from content and check it against the source and target engines.
        BackupPayloadFormat format = BackupArtifactInspector.DetectPayloadFormat(header);
        var description = BackupArtifactInspector.Describe(format, gzip);
        await request.Log($"Detected artifact format: {description}.", cancellationToken);
        if (format == BackupPayloadFormat.Unknown)
            throw new InvalidDataException("Unsupported backup format. Nothing was changed.");
        if (!BackupArtifactInspector.IsCompatible(format, target.Engine) ||
            !BackupArtifactInspector.IsCompatible(format, request.SourceEngine))
            throw new InvalidDataException(
                $"A {description} artifact cannot be restored into a {target.Engine} database. Nothing was changed.");
        if ((options.ReplaceExisting || options.KeepOwners) && format != BackupPayloadFormat.PostgreSqlArchive)
            throw new InvalidDataException(
                "Replace-existing and keep-owners restores require a PostgreSQL archive (custom or tar format). " +
                "Nothing was changed.");

        // 4. Validate and scan the payload before the target is touched.
        var restoreInput = payload;
        RedisPersistence? redis = null;
        switch (target.Engine)
        {
            case DatabaseEngine.PostgreSql when format == BackupPayloadFormat.PostgreSqlArchive:
                ThrowIfBlocked(await ScanPostgresArchiveAsync(cli, container, target, request.Credentials, scratch,
                    payload, cancellationToken));
                break;
            case DatabaseEngine.PostgreSql:
                await using (FileStream script = scratch.OpenRead(payload))
                {
                    ThrowIfBlocked(await PostgresRestoreScriptScanner.ScanAsync(script, cancellationToken));
                }

                break;
            case DatabaseEngine.MySql or DatabaseEngine.MariaDb:
                restoreInput = "restore.sql";
                MySqlFilterResult filtered;
                await using (FileStream dump = scratch.OpenRead(payload))
                await using (FileStream output = scratch.CreateFile(restoreInput))
                {
                    filtered = await MySqlRestoreScriptFilter.FilterAsync(dump, output, cancellationToken);
                }

                ThrowIfBlocked(filtered.Violation);
                if (filtered.SkippedSystemLines > 0)
                    await request.Log(
                        $"Skipped {filtered.SkippedSystemLines} lines that belong to system databases (users, " +
                        "passwords and privileges are not restored).", cancellationToken);
                break;
            case DatabaseEngine.Redis:
                redis = await ReadRedisPersistenceAsync(cli, container, target, request.Credentials,
                    cancellationToken);
                break;
            default:
                throw new InvalidOperationException(
                    "Restore provider supports PostgreSQL, MySQL, MariaDB, and Redis.");
        }

        await request.Log("Safety checks passed.", cancellationToken);
        if (options.DryRun)
            return new DatabaseRestoreOutcome(description,
                "Dry run passed: the artifact was verified and inspected; the target database was not changed.");

        // 5. Restore.
        switch (target.Engine)
        {
            case DatabaseEngine.PostgreSql:
                await RestorePostgresAsync(cli, container, target, request, scratch, restoreInput,
                    format == BackupPayloadFormat.PostgreSqlArchive, cancellationToken);
                break;
            case DatabaseEngine.MySql or DatabaseEngine.MariaDb:
                await RestoreMySqlAsync(cli, container, target, request, scratch, restoreInput, cancellationToken);
                break;
            case DatabaseEngine.Redis:
                await RestoreRedisAsync(cli, container, request, scratch, restoreInput, redis!, cancellationToken);
                break;
        }

        return new DatabaseRestoreOutcome(description, "Restore completed.");
    }

    private async Task<BackupPlan> CreateBackupPlanAsync(string cli, string container, DatabaseResource database,
        string credentials, CancellationToken cancellationToken)
    {
        switch (database.Engine)
        {
            case DatabaseEngine.PostgreSql:
                // The custom archive format is compressed by pg_dump itself and restores through pg_restore,
                // which supports single-transaction, clean and owner options.
                return new BackupPlan("pg_dump",
                [
                    "exec", "--env", "PGPASSWORD", container, "pg_dump", "--username=postgres", "--dbname=postgres",
                    "--format=custom"
                ], false, "postgresql-custom", ".pgdump", "application/octet-stream");
            case DatabaseEngine.MySql or DatabaseEngine.MariaDb:
                {
                    var (client, dump) = MySqlBinaries(database.Engine);
                    IReadOnlyList<string> databases =
                        await ListUserDatabasesAsync(cli, container, database, client, credentials, cancellationToken);
                    if (databases.Count == 0)
                        return new BackupPlan(dump, null, true, "mysql-sql+gzip", ".sql.gz", "application/gzip");
                    var arguments = new List<string>
                {
                    "exec", "--env", "MYSQL_PWD", container, dump, "--user=root", "--single-transaction", "--quick",
                    "--routines", "--events", "--triggers", "--hex-blob", "--databases"
                };
                    arguments.AddRange(databases);
                    return new BackupPlan(dump, arguments, true, "mysql-sql+gzip", ".sql.gz", "application/gzip");
                }
            case DatabaseEngine.Redis:
                return new BackupPlan("redis-cli",
                [
                    "exec", "--env", "REDISCLI_AUTH", container, "redis-cli", "--no-auth-warning", "--rdb", "-"
                ], true, "redis-rdb+gzip", ".rdb.gz", "application/gzip");
            default:
                throw new InvalidOperationException("Backup provider supports PostgreSQL, MySQL, MariaDB, and Redis.");
        }
    }

    private async Task<IReadOnlyList<string>> ListUserDatabasesAsync(string cli, string container,
        DatabaseResource database, string client, string credentials, CancellationToken cancellationToken)
    {
        ProcessResult result = await _processes.RunTextAsync(new ProcessCommand(
            cli,
            [
                "exec", "--env", "MYSQL_PWD", container, client, "--user=root", "--batch", "--skip-column-names",
                "--execute=SHOW DATABASES"
            ],
            Environment: CreateEnvironment(database, credentials),
            Timeout: ControlCommandTimeout,
            Redaction: new ProcessRedactionProfile([credentials], [])), cancellationToken);
        EnsureSucceeded(result, client, cancellationToken);
        string[] names = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name is not ("mysql" or "sys" or "performance_schema" or "information_schema"))
            .ToArray();
        // Names become separate dump arguments; a leading '-' would be parsed as an option.
        if (names.FirstOrDefault(name => name.StartsWith('-')) is { } unsafeName)
            throw new InvalidOperationException(
                $"Database name '{unsafeName}' starts with '-' and cannot be backed up safely.");
        return names;
    }

    private async Task<string?> ScanPostgresArchiveAsync(string cli, string container, DatabaseResource target,
        string credentials, BackupScratchDirectory scratch, string payload, CancellationToken cancellationToken)
    {
        // pg_restore without --dbname prints the archive as SQL; it validates the archive and lets the same scan
        // that guards plain dumps inspect every statement, without writing the script to disk.
        var pipe = new Pipe();
        Task<string?> scan = Task.Run(async () =>
        {
            try
            {
                await using Stream reader = pipe.Reader.AsStream(true);
                return await PostgresRestoreScriptScanner.ScanAsync(reader, cancellationToken);
            }
            finally
            {
                await pipe.Reader.CompleteAsync();
            }
        }, cancellationToken);

        ProcessResult result;
        try
        {
            await using FileStream input = scratch.OpenRead(payload);
            await using Stream writer = pipe.Writer.AsStream(true);
            result = await _processes.RunStreamingAsync(
                Command(cli, ["exec", "--interactive", container, "pg_restore"], target, credentials,
                    MaxProcessDuration),
                new ProcessStreams(input, writer), cancellationToken);
        }
        finally
        {
            await pipe.Writer.CompleteAsync();
        }

        var violation = await scan;
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Succeeded)
            throw new InvalidDataException(
                $"pg_restore cannot read the archive (exit code {result.ExitInfo.ExitCode}): " +
                $"{Tail(result.StandardError)} Nothing was changed.");
        return violation;
    }

    private async Task RestorePostgresAsync(string cli, string container, DatabaseResource target,
        DatabaseRestoreRequest request, BackupScratchDirectory scratch, string input, bool archive,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "exec", "--interactive", "--env", "PGPASSWORD", container };
        if (archive)
        {
            arguments.AddRange(["pg_restore", "--username=postgres", "--dbname=postgres", "--exit-on-error",
                "--single-transaction"]);
            if (!request.Options.KeepOwners) arguments.AddRange(["--no-owner", "--no-privileges"]);
            if (request.Options.ReplaceExisting) arguments.AddRange(["--clean", "--if-exists"]);
        }
        else
        {
            // The script was scanned for meta-commands; ON_ERROR_STOP and a single transaction make the restore
            // all-or-nothing, and --no-psqlrc keeps container-side psqlrc files out of the session.
            arguments.AddRange(["psql", "--username=postgres", "--dbname=postgres", "--no-psqlrc", "--quiet",
                "--variable=ON_ERROR_STOP=1", "--single-transaction", "--file=-"]);
        }

        var tool = archive ? "pg_restore" : "psql";
        await request.Log($"Running {tool} in a single transaction.", cancellationToken);
        await using FileStream stream = scratch.OpenRead(input);
        ProcessResult result = await _processes.RunStreamingAsync(
            Command(cli, arguments, target, request.Credentials, MaxProcessDuration),
            new ProcessStreams(stream), cancellationToken);
        await LogOutputAsync(request, result, cancellationToken);
        if (!result.Succeeded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                $"{tool} failed with exit code {result.ExitInfo.ExitCode}; the transaction was rolled back. " +
                Tail(result.StandardError));
        }
    }

    private async Task RestoreMySqlAsync(string cli, string container, DatabaseResource target,
        DatabaseRestoreRequest request, BackupScratchDirectory scratch, string input,
        CancellationToken cancellationToken)
    {
        var (client, _) = MySqlBinaries(target.Engine);
        // --binary-mode disables every client command except charset and delimiter for piped input, on top of the
        // scan that already rejected shell, file and include commands.
        await request.Log($"Running {client}; it stops at the first failing statement.", cancellationToken);
        await using FileStream stream = scratch.OpenRead(input);
        ProcessResult result = await _processes.RunStreamingAsync(
            Command(cli, ["exec", "--interactive", "--env", "MYSQL_PWD", container, client, "--user=root",
                "--binary-mode"], target, request.Credentials, MaxProcessDuration),
            new ProcessStreams(stream), cancellationToken);
        await LogOutputAsync(request, result, cancellationToken);
        if (!result.Succeeded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                $"{client} failed with exit code {result.ExitInfo.ExitCode}. Statements before the failure may " +
                $"have been applied. {Tail(result.StandardError)}");
        }
    }

    private async Task<RedisPersistence> ReadRedisPersistenceAsync(string cli, string container,
        DatabaseResource target, string credentials, CancellationToken cancellationToken)
    {
        var appendOnly = await RedisConfigAsync(cli, container, target, credentials, "appendonly", cancellationToken);
        if (string.Equals(appendOnly, "yes", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Redis append-only persistence is enabled, so Redis would ignore a restored RDB file. Nothing was " +
                "changed.");
        var directory = await RedisConfigAsync(cli, container, target, credentials, "dir", cancellationToken);
        var fileName = await RedisConfigAsync(cli, container, target, credentials, "dbfilename", cancellationToken);
        if (!SafeContainerPath().IsMatch(directory) || !SafeFileName().IsMatch(fileName))
            throw new InvalidOperationException(
                "The Redis data directory or dump file name is not a plain path. Nothing was changed.");
        return new RedisPersistence($"{directory.TrimEnd('/')}/{fileName}");
    }

    private async Task<string> RedisConfigAsync(string cli, string container, DatabaseResource target,
        string credentials, string parameter, CancellationToken cancellationToken)
    {
        ProcessResult result = await _processes.RunTextAsync(new ProcessCommand(
            cli,
            ["exec", "--env", "REDISCLI_AUTH", container, "redis-cli", "--no-auth-warning", "CONFIG", "GET", parameter],
            Environment: CreateEnvironment(target, credentials),
            Timeout: ControlCommandTimeout,
            Redaction: new ProcessRedactionProfile([credentials], [])), cancellationToken);
        EnsureSucceeded(result, "redis-cli", cancellationToken);
        string[] lines = result.StandardOutput.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length >= 2 && string.Equals(lines[0], parameter, StringComparison.OrdinalIgnoreCase)
            ? lines[1]
            : throw new InvalidOperationException($"Could not read the Redis '{parameter}' setting. Nothing was changed.");
    }

    private async Task RestoreRedisAsync(string cli, string container, DatabaseRestoreRequest request,
        BackupScratchDirectory scratch, string input, RedisPersistence persistence,
        CancellationToken cancellationToken)
    {
        // An RDB snapshot is not a command stream, so it cannot be replayed; Redis loads it at startup instead.
        await request.Log("Stopping the Redis container.", cancellationToken);
        await RunRuntimeAsync(cli, ["stop", "--time", "60", container], request, cancellationToken);
        Exception? failure = null;
        try
        {
            await RunRuntimeAsync(cli, ["cp", scratch.PathFor(input), $"{container}:{persistence.DumpPath}"],
                request, cancellationToken);
            await request.Log($"Replaced {persistence.DumpPath}.", CancellationToken.None);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        // Always bring Redis back, with the restored or the original data.
        await request.Log("Starting the Redis container.", CancellationToken.None);
        await RunRuntimeAsync(cli, ["start", container], request, CancellationToken.None);
        if (failure is not null)
            throw new InvalidOperationException(
                $"Replacing the Redis dump file failed; Redis was restarted with its previous data. {failure.Message}",
                failure);
    }

    private async Task RunRuntimeAsync(string cli, IReadOnlyList<string> arguments, DatabaseRestoreRequest request,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await _processes.RunTextAsync(new ProcessCommand(cli, arguments,
            Timeout: ControlCommandTimeout,
            Redaction: new ProcessRedactionProfile([request.Credentials], [])), cancellationToken);
        EnsureSucceeded(result, $"{cli} {arguments[0]}", cancellationToken);
    }

    private async Task LogOutputAsync(DatabaseRestoreRequest request, ProcessResult result,
        CancellationToken cancellationToken)
    {
        var output = Tail(result.StandardError);
        if (output.Length > 0) await request.Log(output, cancellationToken);
    }

    private ProcessCommand Command(string cli, IReadOnlyList<string> arguments, DatabaseResource database,
        string credentials, TimeSpan timeout)
    {
        return new ProcessCommand(
            cli,
            arguments,
            Environment: CreateEnvironment(database, credentials),
            Timeout: timeout,
            OutputMode: ProcessOutputMode.Stream,
            Redaction: new ProcessRedactionProfile([credentials], []),
            MaxOutputBytes: DiagnosticOutputLimit);
    }

    private static void EnsureSucceeded(ProcessResult result, string tool, CancellationToken cancellationToken)
    {
        if (result.Succeeded) return;
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitInfo.TimedOut) throw new TimeoutException($"{tool} timed out.");
        throw new InvalidOperationException(
            $"{tool} failed with exit code {result.ExitInfo.ExitCode}. {Tail(result.StandardError)}".TrimEnd());
    }

    private static void ThrowIfBlocked(string? violation)
    {
        if (violation is null) return;
        throw new InvalidDataException(
            $"Blocked restore: the backup contains {violation}. Nothing was changed. If you trust this dump, " +
            "restore it manually from the database container.");
    }

    private static async Task<byte[]> ReadHeaderAsync(BackupScratchDirectory scratch, string fileName,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = scratch.OpenRead(fileName);
        var buffer = new byte[BackupArtifactInspector.HeaderSampleLength];
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, cancellationToken);
        return buffer[..read];
    }

    private static string Tail(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= LogTailLength ? trimmed : "..." + trimmed[^LogTailLength..];
    }

    private static (string Client, string Dump) MySqlBinaries(DatabaseEngine engine)
    {
        // MariaDB 11+ images no longer ship the mysql* compatibility names.
        return engine == DatabaseEngine.MariaDb ? ("mariadb", "mariadb-dump") : ("mysql", "mysqldump");
    }

    private static IReadOnlyDictionary<string, string?> CreateEnvironment(DatabaseResource database,
        string credentials)
    {
        return database.Engine switch
        {
            DatabaseEngine.PostgreSql => new Dictionary<string, string?> { ["PGPASSWORD"] = credentials },
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => new Dictionary<string, string?>
            { ["MYSQL_PWD"] = credentials },
            DatabaseEngine.Redis => new Dictionary<string, string?> { ["REDISCLI_AUTH"] = credentials },
            _ => throw new InvalidOperationException("Backup provider supports PostgreSQL, MySQL, MariaDB, and Redis.")
        };
    }

    private static string RequireContainerName(DatabaseResource database)
    {
        return string.IsNullOrWhiteSpace(database.ContainerName)
            ? throw new InvalidOperationException("Database container name is required for backup and restore.")
            : database.ContainerName;
    }

    [GeneratedRegex(@"^/[A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeContainerPath();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeFileName();

    private sealed record BackupPlan(
        string Tool,
        IReadOnlyList<string>? Arguments,
        bool Gzip,
        string Format,
        string Extension,
        string ContentType);

    private sealed record RedisPersistence(string DumpPath);
}
