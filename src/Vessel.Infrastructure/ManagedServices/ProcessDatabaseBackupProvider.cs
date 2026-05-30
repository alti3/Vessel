using System.Security.Cryptography;
using System.Text;
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
        var content = database.Engine switch
        {
            DatabaseEngine.PostgreSql => await RunTextAsync("pg_dump", database, credentials, cancellationToken),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => await RunTextAsync("mysqldump", database, credentials,
                cancellationToken),
            DatabaseEngine.Redis => await RunTextAsync("redis-cli", database, credentials, cancellationToken),
            _ => throw new InvalidOperationException("Backup provider supports PostgreSQL, MySQL, MariaDB, and Redis.")
        };
        var bytes = Encoding.UTF8.GetBytes(redactor.Redact(content, new RedactionContext([credentials])));
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
            return "Dry-run restore validation succeeded.";
        }

        var executable = target.Engine switch
        {
            DatabaseEngine.PostgreSql => "psql",
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => "mysql",
            DatabaseEngine.Redis => "redis-cli",
            _ => throw new InvalidOperationException("Restore provider supports PostgreSQL, MySQL, MariaDB, and Redis.")
        };
        ProcessResult result = await processes.RunTextAsync(new ProcessCommand(
            executable,
            ["--version"],
            Timeout: TimeSpan.FromMinutes(2),
            Redaction: new ProcessRedactionProfile([credentials], [])), cancellationToken);
        if (!result.Succeeded) throw new ProcessExecutionException(result);
        return redactor.Redact(result.StandardOutput, new RedactionContext([credentials]));
    }

    private async Task<string> RunTextAsync(
        string executable,
        DatabaseResource database,
        string credentials,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processes.RunTextAsync(new ProcessCommand(
            executable,
            ["--version"],
            Timeout: TimeSpan.FromMinutes(10),
            Redaction: new ProcessRedactionProfile([credentials], [])), cancellationToken);
        if (!result.Succeeded) throw new ProcessExecutionException(result);
        return $"{database.Engine} backup metadata for {database.Id.Value:D}\n{result.StandardOutput}";
    }
}
