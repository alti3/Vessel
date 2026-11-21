using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Vessel.Application.Files;
using Vessel.Application.Operations.ControlPlaneBackups;
using Vessel.Application.Processes;
using Vessel.Infrastructure.Configuration;

namespace Vessel.Infrastructure.Operations;

public sealed class ControlPlaneBackupOptions
{
    public const string SectionName = "Vessel:Operations:ControlPlaneBackups";

    /// <summary>Owned root of control-plane dumps (subfolders <c>manual/</c>, <c>pre-update/</c>, <c>scheduled/</c>).</summary>
    public string Directory { get; init; } = "/var/lib/vessel/backups/control-plane";

    /// <summary>The <c>pg_dump</c> executable; its major version must be at least the server's.</summary>
    public string PgDumpPath { get; init; } = "pg_dump";

    public int TimeoutMinutes { get; init; } = 30;

    /// <summary>Below this much free space a warning is logged; the dump still runs (upstream reverted its hard block).</summary>
    public long LowDiskWarningBytes { get; init; } = 1024L * 1024 * 1024;
}

/// <summary>
///     Dumps Vessel's own PostgreSQL database with <c>pg_dump --format=custom</c> through <see cref="IProcessRunner" />
///     (DATA-13 / OPS-02). Host, port, user and database are structured arguments; the password is passed only through
///     <c>PGPASSWORD</c> in a child environment that inherits nothing else from the daemon (no connection strings or
///     master keys), and it is redacted from all captured output. The dump is written to a temporary name in the owned
///     backup directory, renamed atomically, and accompanied by a <c>sha256sum</c>-format checksum file. Files are
///     created with owner-only permissions: a dump contains every Vessel row (secrets stay encrypted at rest).
/// </summary>
public sealed class PgDumpControlPlaneDatabaseDumper(
    IProcessRunner processRunner,
    IPathSafetyService pathSafety,
    IOptions<ControlPlaneBackupOptions> options,
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<PgDumpControlPlaneDatabaseDumper> logger) : IControlPlaneDatabaseDumper
{
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public async Task<ControlPlaneDumpResult> DumpAsync(ControlPlaneDumpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ControlPlaneDumpException("No Vessel database connection is configured.");
        NpgsqlConnectionStringBuilder connection;
        try
        {
            connection = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new ControlPlaneDumpException("The Vessel database connection string is not valid.");
        }

        if (string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.Database) ||
            string.IsNullOrWhiteSpace(connection.Username))
            throw new ControlPlaneDumpException("The Vessel database connection needs a host, database and username.");

        var root = EnsureRoot();
        var finalPath = pathSafety.EnsureNoSymlinks(root, request.RelativePath);
        var directory = Path.GetDirectoryName(finalPath)!;
        CreateOwnedDirectory(directory);
        if (File.Exists(finalPath)) throw new ControlPlaneDumpException("A dump with this name already exists.");
        var temporaryPath = pathSafety.EnsureNoSymlinks(root, request.RelativePath + ".partial");
        WarnOnLowDiskSpace(root);

        ProcessCommand command = BuildCommand(connection, temporaryPath,
            request.Timeout ?? TimeSpan.FromMinutes(Math.Max(1, options.Value.TimeoutMinutes)));
        try
        {
            ProcessResult result = await processRunner.RunTextAsync(command, cancellationToken);
            if (result.ExitInfo.TimedOut)
                throw new ControlPlaneDumpException("pg_dump exceeded its timeout and was stopped.");
            if (result.ExitInfo.Canceled) throw new OperationCanceledException(cancellationToken);
            if (result.ExitInfo.ExitCode != 0)
                throw new ControlPlaneDumpException(
                    $"pg_dump exited with code {result.ExitInfo.ExitCode}: {Tail(result.StandardError)}");
            if (!File.Exists(temporaryPath)) throw new ControlPlaneDumpException("pg_dump did not write a dump file.");

            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporaryPath, OwnerOnlyFile);
            string sha256;
            long size;
            await using (FileStream stream = File.OpenRead(temporaryPath))
            {
                size = stream.Length;
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            }

            File.Move(temporaryPath, finalPath, false);
            var checksumPath = finalPath + ".sha256";
            await File.WriteAllTextAsync(checksumPath, $"{sha256}  {Path.GetFileName(finalPath)}\n",
                CancellationToken.None);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(checksumPath, OwnerOnlyFile);
            return new ControlPlaneDumpResult(request.RelativePath, size, sha256);
        }
        catch (Exception exception) when (exception is not ControlPlaneDumpException and not OperationCanceledException)
        {
            logger.LogError(exception, "Control-plane database dump failed.");
            throw new ControlPlaneDumpException("The database dump could not be written.");
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public Stream OpenRead(string relativePath)
    {
        var path = pathSafety.EnsureNoSymlinks(EnsureRoot(), relativePath);
        if (!File.Exists(path)) throw new KeyNotFoundException("The dump file no longer exists.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
    }

    public void Delete(string relativePath)
    {
        var path = pathSafety.EnsureNoSymlinks(EnsureRoot(), relativePath);
        TryDelete(path);
        TryDelete(path + ".sha256");
    }

    /// <summary>The <c>pg_dump</c> invocation; public for tests. Never contains the password.</summary>
    public ProcessCommand BuildCommand(NpgsqlConnectionStringBuilder connection, string outputPath, TimeSpan timeout)
    {
        var arguments = new List<string>
        {
            "--format=custom",
            "--no-password",
            $"--file={outputPath}",
            $"--host={connection.Host}",
            $"--port={connection.Port}",
            $"--username={connection.Username}",
            $"--dbname={connection.Database}"
        };
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["PGCONNECT_TIMEOUT"] = "15"
        };
        var secrets = new List<string>();
        if (!string.IsNullOrEmpty(connection.Password))
        {
            environment["PGPASSWORD"] = connection.Password;
            secrets.Add(connection.Password);
        }

        var sslMode = connection.SslMode switch
        {
            SslMode.Disable => "disable",
            SslMode.Allow => "allow",
            SslMode.Require => "require",
            SslMode.VerifyCA => "verify-ca",
            SslMode.VerifyFull => "verify-full",
            _ => "prefer"
        };
        environment["PGSSLMODE"] = sslMode;

        return new ProcessCommand(options.Value.PgDumpPath, arguments, Environment: environment, Timeout: timeout,
            Redaction: new ProcessRedactionProfile(secrets, []), MaxOutputBytes: 64 * 1024,
            AuditMetadata: new Dictionary<string, string> { ["operation"] = "control-plane-backup" },
            InheritEnvironment: false);
    }

    private string EnsureRoot()
    {
        var root = Path.GetFullPath(options.Value.Directory);
        CreateOwnedDirectory(root);
        return root;
    }

    private static void CreateOwnedDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, OwnerOnlyDirectory);
    }

    private void WarnOnLowDiskSpace(string root)
    {
        try
        {
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < options.Value.LowDiskWarningBytes)
                logger.LogWarning("Only {FreeBytes} bytes are free for control-plane database dumps.", free);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Free space is advisory only.
        }
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[^500..];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
