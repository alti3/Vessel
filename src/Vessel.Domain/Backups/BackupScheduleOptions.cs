using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.Backups;

/// <summary>Dump format of a backup run.</summary>
public enum BackupDumpFormat
{
    /// <summary>
    ///     The engine's native restorable format: PostgreSQL custom archive (compressed, restored with
    ///     <c>pg_restore</c>), MySQL/MariaDB SQL (gzip) and Redis RDB (gzip).
    /// </summary>
    Native = 0,

    /// <summary>Plain SQL (gzip). For PostgreSQL this is <c>pg_dump --format=plain</c>; other engines use Native.</summary>
    PlainSql = 1
}

/// <summary>
///     Which logical databases a schedule dumps. <see cref="All" /> dumps every user database (for PostgreSQL one
///     execution per database, for MySQL/MariaDB one dump of every user database), <see cref="Named" /> dumps the
///     listed databases (one execution per database) and <see cref="Default" /> dumps the engine's default database
///     only (PostgreSQL <c>postgres</c>; whole instance for other engines).
/// </summary>
public readonly partial record struct BackupDatabaseSelection
{
    public const int MaxNames = 50;

    public const int MaxStoredLength = 2000;

    private const string AllMarker = "*";

    private BackupDatabaseSelection(BackupDatabaseSelectionMode mode, IReadOnlyList<string> names)
    {
        Mode = mode;
        _names = names;
    }

    private readonly IReadOnlyList<string>? _names;

    public BackupDatabaseSelectionMode Mode { get; }

    public IReadOnlyList<string> Names => _names ?? [];

    public static BackupDatabaseSelection All { get; } = new(BackupDatabaseSelectionMode.All, []);

    public static BackupDatabaseSelection Default { get; } = new(BackupDatabaseSelectionMode.Default, []);

    public static BackupDatabaseSelection Named(IEnumerable<string> names)
    {
        string[] normalized = names.Select(name => name.Trim()).Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (normalized.Length == 0) throw new DomainException("Select at least one database to back up.");
        if (normalized.Length > MaxNames)
            throw new DomainException($"A backup schedule can select at most {MaxNames} databases.");
        foreach (var name in normalized) ValidateName(name);
        return new BackupDatabaseSelection(BackupDatabaseSelectionMode.Named, normalized);
    }

    /// <summary>
    ///     Validates one logical database name. Names become separate tool arguments, so they may not start with
    ///     <c>-</c> and may only contain characters that every supported engine allows unquoted.
    /// </summary>
    public static string ValidateName(string name)
    {
        var value = DomainValidation.Required(name, "Database name", 63);
        return DatabaseNamePattern().IsMatch(value)
            ? value
            : throw new DomainException(
                $"Database name '{value}' is invalid: use letters, digits, '_', '-', '.' or '$' and start with a " +
                "letter, digit or '_'.");
    }

    public string ToStorage()
    {
        return Mode switch
        {
            BackupDatabaseSelectionMode.All => AllMarker,
            BackupDatabaseSelectionMode.Named => string.Join(',', Names),
            _ => string.Empty
        };
    }

    public static BackupDatabaseSelection FromStorage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Default;
        return value.Trim() == AllMarker ? All : Named(value.Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

    public override string ToString()
    {
        return Mode switch
        {
            BackupDatabaseSelectionMode.All => "all databases",
            BackupDatabaseSelectionMode.Named => string.Join(", ", Names),
            _ => "default database"
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.$-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseNamePattern();
}

public enum BackupDatabaseSelectionMode
{
    Default = 0,
    All = 1,
    Named = 2
}

/// <summary>
///     Retention for one copy location (local or remote). Each limit is optional (0 = unlimited) and they combine:
///     an artifact is pruned when it is beyond <see cref="Count" /> newest, older than <see cref="Days" />, or pushes
///     the total size over <see cref="MaxBytes" /> (oldest first). Protected artifacts are never pruned.
/// </summary>
public readonly record struct BackupRetentionPolicy
{
    public const int MaxCount = 500;

    public const int MaxDays = 3650;

    public BackupRetentionPolicy(int count, int days, long maxBytes)
    {
        if (count is < 0 or > MaxCount)
            throw new DomainException($"Backup retention count must be between 0 and {MaxCount}.");
        if (days is < 0 or > MaxDays)
            throw new DomainException($"Backup retention days must be between 0 and {MaxDays}.");
        if (maxBytes < 0) throw new DomainException("Backup retention size cannot be negative.");
        Count = count;
        Days = days;
        MaxBytes = maxBytes;
    }

    public int Count { get; }

    public int Days { get; }

    public long MaxBytes { get; }

    public bool IsUnlimited => Count == 0 && Days == 0 && MaxBytes == 0;

    /// <summary>
    ///     Returns the artifacts to prune. <paramref name="newestFirst" /> must be ordered newest first and must already
    ///     exclude protected artifacts.
    /// </summary>
    public IReadOnlyList<T> SelectExpired<T>(IReadOnlyList<T> newestFirst, Func<T, DateTimeOffset> createdAt,
        Func<T, long> sizeBytes, DateTimeOffset now)
    {
        if (IsUnlimited) return [];
        var expired = new List<T>();
        long total = 0;
        for (var index = 0; index < newestFirst.Count; index++)
        {
            T item = newestFirst[index];
            total += Math.Max(0, sizeBytes(item));
            var overCount = Count > 0 && index >= Count;
            var tooOld = Days > 0 && createdAt(item) < now.AddDays(-Days);
            // The newest artifact is always kept by the size rule, even if it alone exceeds the budget.
            var overSize = MaxBytes > 0 && index > 0 && total > MaxBytes;
            if (overCount || tooOld || overSize) expired.Add(item);
        }

        return expired;
    }
}
