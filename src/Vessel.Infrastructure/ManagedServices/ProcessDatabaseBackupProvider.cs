using System.Security.Cryptography;
using Vessel.Application.ManagedServices;
using Vessel.Application.Processes;
using Vessel.Application.Security;
using Vessel.Domain.Databases;

namespace Vessel.Infrastructure.ManagedServices;

public sealed class ProcessDatabaseBackupProvider(
    IProcessRunner processes,
    ISecretRedactor redactor,
    TimeProvider timeProvider) : IDatabaseBackupProvider
{
    public async Task<BackupArtifact> BackupAsync(
        DatabaseResource database,
        string credentials,
        CancellationToken cancellationToken = default)
    {
        ProcessBinaryResult result = await processes.RunBinaryAsync(new ProcessCommand(
            "docker",
            CreateBackupArguments(database),
            Environment: CreateEnvironment(database, credentials),
            Timeout: TimeSpan.FromMinutes(10),
            OutputMode: ProcessOutputMode.Binary,
            Redaction: new ProcessRedactionProfile([credentials], [])), cancellationToken);
        if (!result.Succeeded) throw CreateBinaryException(result);

        byte[] bytes = result.StandardOutput.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var stream = new MemoryStream(bytes, false);
        var key = $"database/{database.Id.Value:D}/{timeProvider.GetUtcNow():yyyyMMddHHmmss}-{sha[..12]}.dump";
        return new BackupArtifact("vessel-backups", key, bytes.Length, sha, stream);
    }

    public async Task<string> RestoreAsync(
        DatabaseResource target,
        string credentials,
        Stream artifact,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (dryRun)
        {
            if (!artifact.CanRead) throw new InvalidOperationException("Backup artifact is not readable.");
            ProcessResult dryRunResult = await processes.RunTextAsync(new ProcessCommand(
                "docker",
                CreateDryRunArguments(target),
                Timeout: TimeSpan.FromMinutes(2),
                Redaction: new ProcessRedactionProfile([credentials], [])), cancellationToken);
            if (!dryRunResult.Succeeded) throw new ProcessExecutionException(dryRunResult);
            return "Dry-run restore validation succeeded.";
        }

        ProcessResult result = await processes.RunTextWithInputAsync(new ProcessCommand(
            "docker",
            CreateRestoreArguments(target),
            Environment: CreateEnvironment(target, credentials),
            Timeout: TimeSpan.FromMinutes(2),
            Redaction: new ProcessRedactionProfile([credentials], [])), artifact, cancellationToken);
        if (!result.Succeeded) throw new ProcessExecutionException(result);
        return redactor.Redact(result.StandardOutput, new RedactionContext([credentials]));
    }

    private static IReadOnlyList<string> CreateBackupArguments(DatabaseResource database)
    {
        var containerName = RequireContainerName(database);
        return database.Engine switch
        {
            DatabaseEngine.PostgreSql =>
            [
                "exec", "--env", "PGPASSWORD", containerName, "pg_dump", "--username", "postgres", "--dbname",
                "postgres", "--format", "plain", "--no-owner", "--no-privileges"
            ],
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
            [
                "exec", "--env", "MYSQL_PWD", containerName, "mysqldump", "--user", "root", "--all-databases",
                "--single-transaction", "--quick"
            ],
            DatabaseEngine.Redis =>
            [
                "exec", "--env", "REDISCLI_AUTH", containerName, "redis-cli", "--no-auth-warning", "--rdb", "-"
            ],
            _ => throw new InvalidOperationException("Backup provider supports PostgreSQL, MySQL, MariaDB, and Redis.")
        };
    }

    private static IReadOnlyList<string> CreateRestoreArguments(DatabaseResource database)
    {
        var containerName = RequireContainerName(database);
        return database.Engine switch
        {
            DatabaseEngine.PostgreSql =>
            [
                "exec", "--interactive", "--env", "PGPASSWORD", containerName, "psql", "--username", "postgres",
                "--dbname", "postgres", "--set", "ON_ERROR_STOP=on"
            ],
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
            [
                "exec", "--interactive", "--env", "MYSQL_PWD", containerName, "mysql", "--user", "root"
            ],
            DatabaseEngine.Redis =>
            [
                "exec", "--interactive", "--env", "REDISCLI_AUTH", containerName, "redis-cli", "--no-auth-warning",
                "--pipe"
            ],
            _ => throw new InvalidOperationException("Restore provider supports PostgreSQL, MySQL, MariaDB, and Redis.")
        };
    }

    private static IReadOnlyList<string> CreateDryRunArguments(DatabaseResource database)
    {
        var containerName = RequireContainerName(database);
        return database.Engine switch
        {
            DatabaseEngine.PostgreSql => ["exec", containerName, "pg_dump", "--version"],
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => ["exec", containerName, "mysqldump", "--version"],
            DatabaseEngine.Redis => ["exec", containerName, "redis-cli", "--version"],
            _ => throw new InvalidOperationException("Restore provider supports PostgreSQL, MySQL, MariaDB, and Redis.")
        };
    }

    private static IReadOnlyDictionary<string, string?> CreateEnvironment(DatabaseResource database, string credentials)
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

    private static ProcessExecutionException CreateBinaryException(ProcessBinaryResult result)
    {
        return new ProcessExecutionException(new ProcessResult(
            result.Command,
            result.ExitInfo,
            result.ProcessId,
            result.StartedAt,
            result.ExitedAt,
            string.Empty,
            System.Text.Encoding.UTF8.GetString(result.StandardError.Span)));
    }
}
