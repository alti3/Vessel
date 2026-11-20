using System.Text;
using System.Text.RegularExpressions;

namespace Vessel.Infrastructure.ManagedServices.Backups;

public sealed record MySqlFilterResult(string? Violation, long SkippedSystemLines);

/// <summary>
/// Streams a MySQL/MariaDB SQL dump into the script Vessel will restore. The system databases (<c>mysql</c>,
/// <c>sys</c>, <c>performance_schema</c>, <c>information_schema</c>) are dropped, because restoring them would
/// replace users, passwords and privileges, including the credentials Vessel stores for this database. Client
/// commands that run a shell command or use a file (<c>system</c>, <c>tee</c>, <c>pager</c>, <c>edit</c>,
/// <c>source</c> and their <c>\!</c>, <c>\T</c>, <c>\P</c>, <c>\e</c>, <c>\.</c> short forms) are rejected.
/// </summary>
/// <remarks>Ported from upstream Coolify's <c>DatabaseImportCommandBuilder</c> filter and client-command scan.</remarks>
public static class MySqlRestoreScriptFilter
{
    /// <summary>Header Vessel writes when a MySQL/MariaDB server has no user databases to dump.</summary>
    public const string EmptyDumpHeader = "-- Vessel MySQL backup: no user databases";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // An odd backslash run starts a short client command; mysqldump writes a backslash in data as "\\" and escapes
    // only 0, n, r, Z and quotes, so its rows do not match.
    private static readonly Regex ShortClientCommand = new(@"(?:^|[^\\])(?:\\\\)*\\[!TPe.]",
        RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex NamedClientCommand = new(@"^\s*(?:system|tee|pager|edit|source)(?:\s|;|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex UseStatement = new(@"^\s*use\s", RegexOptions.IgnoreCase |
                                                                  RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex CreateDatabase = new(@"^\s*create\s+(?:database|schema)\s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex VersionComment = new(@"/\*[^*]*\*/", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly HashSet<string> SystemDatabases =
        new(["mysql", "sys", "performance_schema", "information_schema"], StringComparer.Ordinal);

    /// <summary>
    /// Copies the restorable part of <paramref name="dump" /> to <paramref name="output" /> and reports the first
    /// blocked client command. The output must not be restored when a violation is returned.
    /// </summary>
    public static async Task<MySqlFilterResult> FilterAsync(Stream dump, Stream output,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(dump, new UTF8Encoding(false), false, 81920, true);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 81920, true);
        writer.NewLine = "\n";
        var skip = false;
        long skipped = 0;
        long lineNumber = 0;
        string? violation = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            if (violation is not null) continue;
            try
            {
                if (line.StartsWith("-- Current Database: `", StringComparison.Ordinal))
                    skip = IsSystem(DatabaseName(line));
                if (UseStatement.IsMatch(line)) skip = IsSystem(DatabaseName(line));
                if (CreateDatabase.IsMatch(line))
                {
                    if (IsSystem(DatabaseName(line)))
                    {
                        skipped++;
                        continue;
                    }

                    skip = false;
                }

                if (line.StartsWith("/*!40103 SET TIME_ZONE=@OLD_TIME_ZONE", StringComparison.Ordinal)) skip = false;
                if (skip)
                {
                    skipped++;
                    continue;
                }

                if (ShortClientCommand.IsMatch(line) || NamedClientCommand.IsMatch(line))
                {
                    violation = $"a client command that runs a shell command or uses a file (line {lineNumber})";
                    continue;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                violation = $"a line that could not be scanned in time (line {lineNumber})";
                continue;
            }

            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);
        return new MySqlFilterResult(violation, skipped);
    }

    internal static string DatabaseName(string line)
    {
        var tick = line.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            var rest = line[(tick + 1)..];
            var end = rest.IndexOf('`', StringComparison.Ordinal);
            return (end >= 0 ? rest[..end] : rest).ToLowerInvariant();
        }

        var value = VersionComment.Replace(line, " ").ToLowerInvariant().TrimStart();
        value = Regex.Replace(value, @"^(?:use|create\s+(?:database|schema))\s+", string.Empty,
            RegexOptions.CultureInvariant, RegexTimeout);
        value = Regex.Replace(value, @"^if\s+not\s+exists\s+", string.Empty, RegexOptions.CultureInvariant,
            RegexTimeout);
        var stop = value.IndexOfAny([' ', '\t', ';']);
        return stop >= 0 ? value[..stop] : value;
    }

    private static bool IsSystem(string name)
    {
        return SystemDatabases.Contains(name);
    }
}
