using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.Backups;

/// <summary>Where a database import reads its dump from.</summary>
public enum DatabaseImportSourceKind
{
    /// <summary>Not an import: the restore reads a Vessel backup execution.</summary>
    None = 0,

    /// <summary>A file uploaded in chunks to owned staging storage.</summary>
    Upload = 1,

    /// <summary>A file on the database server's host (instance administrators only).</summary>
    ServerPath = 2,

    /// <summary>An object in one of the team's backup destinations.</summary>
    Destination = 3
}

/// <summary>
///     A dump uploaded in sequential chunks for a database import (upstream <c>/imports/uploads</c>). The bytes live in
///     owned staging storage; the record tracks progress, completion and expiry (staged uploads are deleted after
///     <see cref="Retention" />).
/// </summary>
public sealed partial class DatabaseImportUpload : Entity<DatabaseImportUploadId>
{
    /// <summary>Upstream <c>StartDatabaseImport::MAX_BYTES</c> (10 GiB).</summary>
    public const long DefaultMaxBytes = 10L * 1024 * 1024 * 1024;

    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>Accepted dump file name endings (upstream <c>DatabaseBackupFileValidator</c>).</summary>
    public static readonly IReadOnlyList<string> AllowedExtensions =
    [
        ".sql", ".sql.gz", ".gz", ".dump", ".pgdump", ".tar", ".rdb", ".rdb.gz", ".bak"
    ];

    private DatabaseImportUpload()
    {
    }

    private DatabaseImportUpload(DatabaseImportUploadId id, TeamId teamId, DatabaseResourceId databaseResourceId,
        UserId createdByUserId, string fileName, long declaredSizeBytes, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        DatabaseResourceId = databaseResourceId;
        CreatedByUserId = createdByUserId;
        FileName = fileName;
        DeclaredSizeBytes = declaredSizeBytes;
        ExpiresAt = createdAt + Retention;
    }

    public TeamId TeamId { get; private set; }

    public DatabaseResourceId DatabaseResourceId { get; private set; }

    public UserId CreatedByUserId { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public long DeclaredSizeBytes { get; private set; }

    public long ReceivedBytes { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Set when an import consumed the upload; the staged bytes are deleted.</summary>
    public DateTimeOffset? ConsumedAt { get; private set; }

    public bool IsComplete => CompletedAt is not null;

    public static DatabaseImportUpload Start(TeamId teamId, DatabaseResourceId databaseResourceId,
        UserId createdByUserId, string fileName, long declaredSizeBytes, long maxBytes, DateTimeOffset now)
    {
        if (declaredSizeBytes <= 0) throw new DomainException("Upload size must be greater than zero.");
        if (declaredSizeBytes > maxBytes)
            throw new DomainException($"Uploads are limited to {maxBytes / (1024 * 1024)} MiB.");
        return new DatabaseImportUpload(DatabaseImportUploadId.New(), teamId, databaseResourceId, createdByUserId,
            ValidateFileName(fileName), declaredSizeBytes, now);
    }

    /// <summary>Validates a client-supplied dump file name (a bare name with an allowed extension).</summary>
    public static string ValidateFileName(string fileName)
    {
        var value = DomainValidation.Required(fileName, "File name", 255);
        if (!FileNamePattern().IsMatch(value) || value.Contains("..", StringComparison.Ordinal))
            throw new DomainException(
                "File name may only contain letters, digits, '.', '_' and '-' and must not contain a path.");
        if (!AllowedExtensions.Any(extension => value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException(
                $"Unsupported file type. Allowed: {string.Join(", ", AllowedExtensions)}.");
        return value;
    }

    /// <summary>Checks that a chunk continues the upload at <paramref name="offset" /> and fits the declared size.</summary>
    public void EnsureCanAppend(long offset, long chunkBytes, DateTimeOffset now)
    {
        if (IsComplete || ConsumedAt is not null) throw new DomainException("The upload is already complete.");
        if (now >= ExpiresAt) throw new DomainException("The upload expired; start a new upload.");
        if (offset != ReceivedBytes)
            throw new DomainException($"Chunk offset {offset} does not continue the upload at {ReceivedBytes}.");
        if (chunkBytes < 0 || ReceivedBytes + chunkBytes > DeclaredSizeBytes)
            throw new DomainException("The chunk exceeds the declared upload size.");
    }

    public void RecordChunk(long offset, long chunkBytes, DateTimeOffset now)
    {
        EnsureCanAppend(offset, chunkBytes, now);
        ReceivedBytes += chunkBytes;
        Touch(now);
    }

    public void Complete(DateTimeOffset now)
    {
        if (IsComplete) return;
        if (ReceivedBytes != DeclaredSizeBytes)
            throw new DomainException(
                $"The upload is incomplete: {ReceivedBytes} of {DeclaredSizeBytes} bytes were received.");
        CompletedAt = now;
        Touch(now);
    }

    public void MarkConsumed(DateTimeOffset now)
    {
        ConsumedAt ??= now;
        Touch(now);
    }

    public bool IsExpired(DateTimeOffset now)
    {
        return now >= ExpiresAt;
    }

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._-]{0,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
}
