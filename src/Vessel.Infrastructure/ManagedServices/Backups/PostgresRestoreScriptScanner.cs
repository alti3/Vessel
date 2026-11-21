using System.Text;
using System.Text.RegularExpressions;

namespace Vessel.Infrastructure.ManagedServices.Backups;

/// <summary>
/// Streams a PostgreSQL SQL script (a plain dump, or the SQL that <c>pg_restore --file=-</c> prints for an archive)
/// and reports constructs that would let a restore run code outside the database: psql meta-commands that start
/// a shell, write or include files (<c>\!</c>, <c>\o |</c>, <c>\copy ... program</c>, <c>\i</c>, backquotes ...),
/// <c>COPY ... TO/FROM PROGRAM</c>, server-side <c>lo_import</c>/<c>lo_export</c>, and <c>ALTER SYSTEM</c>.
/// </summary>
/// <remarks>
/// The rules follow upstream Coolify's restore scan (<c>DatabaseBackupFileValidator</c> and
/// <c>DatabaseImportCommandBuilder::buildPostgresRestoreScanScript</c>) and err on the side of blocking: SQL
/// patterns are matched in code, in comment-stripped text and in raw text (string literals and function bodies
/// included). Table data of <c>COPY ... FROM stdin</c> blocks is skipped only when the COPY line starts outside a
/// string, dollar quote or comment, so a fake COPY line inside a literal cannot hide what follows. Static scanning
/// cannot see through every form of dynamic SQL; Vessel additionally restores only artifacts whose SHA-256 matches
/// the checksum recorded when the backup was taken.
/// </remarks>
public static class PostgresRestoreScriptScanner
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // Port of upstream's psql meta-command pattern. An odd run of backslashes starts a meta-command; COPY data
    // writes a literal backslash as "\\", so its rows do not match. \n, \b, \v and \N are COPY escapes psql does not
    // know, and psql does not expand backquotes for unknown commands.
    private static readonly Regex PsqlMetaCommand = new(
        @"(?:^|[^\\])(?:\\\\)*\\(?:!|(?:o|out|gx?|w|write)[^\S\n][^\\\n]*\||copy[^\S\n].*program|(?:setenv|e|edit|ef|ev|i|ir|include|include_relative)(?:\s|$)|(?:[^nbv\s\\]|[nbv][^\s\\])[^\s\\]*[^\S\n][^\\\n]*`)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex CopyFromStdin = new(@"^COPY .* FROM stdin;$",
        RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>Returns a description of the first blocked construct, or null when the script passes.</summary>
    /// <remarks>The whole stream is always consumed, so a producing process is never blocked on a full pipe.</remarks>
    public static async Task<string?> ScanAsync(Stream script, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(script, new UTF8Encoding(false), false, 81920, true);
        var lexer = new SqlLexer();
        var code = new SqlPatternDetector("in SQL");
        var commentStripped = new SqlPatternDetector("in SQL or a string literal");
        var raw = new SqlPatternDetector("in a string literal or function body");
        var commentStripper = new CommentStripper();
        string? violation = null;
        var inCopyData = false;
        long lineNumber = 0;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            if (violation is not null) continue;
            if (inCopyData)
            {
                if (line == "\\.") inCopyData = false;
                continue;
            }

            var startsAtTopLevel = lexer.IsTopLevel;
            try
            {
                if (PsqlMetaCommand.IsMatch(line))
                {
                    violation = $"a psql meta-command that can run a shell command or use a file (line {lineNumber})";
                    continue;
                }

                lexer.Feed(line, code);
                commentStripper.Feed(line, commentStripped);
                SqlPatternDetector.FeedRaw(line, raw);
                if ((code.Violation ?? commentStripped.Violation ?? raw.Violation) is { } pattern)
                {
                    violation = $"{pattern} (line {lineNumber})";
                    continue;
                }

                if (startsAtTopLevel && lexer.IsTopLevel && CopyFromStdin.IsMatch(line)) inCopyData = true;
            }
            catch (RegexMatchTimeoutException)
            {
                violation = $"a line that could not be scanned in time (line {lineNumber})";
            }
        }

        return violation;
    }

    /// <summary>Tracks the last tokens of one view of the script and flags blocked token sequences.</summary>
    private sealed class SqlPatternDetector(string where)
    {
        private string? _previous;
        private string? _beforePrevious;

        public string? Violation { get; private set; }

        public static void FeedRaw(string line, SqlPatternDetector detector)
        {
            var index = 0;
            while (index < line.Length)
            {
                var character = line[index];
                if (char.IsWhiteSpace(character))
                {
                    index++;
                    continue;
                }

                if (IsWordCharacter(character))
                {
                    var start = index;
                    while (index < line.Length && IsWordCharacter(line[index])) index++;
                    detector.Push(line[start..index]);
                    continue;
                }

                detector.Push(character.ToString());
                index++;
            }
        }

        public void Push(string token)
        {
            if (Violation is not null) return;
            var current = token.ToLowerInvariant();
            if (current == "program" && _previous is "from" or "to")
                Violation = $"COPY ... {_previous!.ToUpperInvariant()} PROGRAM {where}";
            else if (current == "(" && _previous is "lo_import" or "lo_export")
                Violation = $"a server-side {_previous} call {where}";
            else if (current is "set" or "reset" && _previous == "system" && _beforePrevious == "alter")
                Violation = $"ALTER SYSTEM {where}";
            _beforePrevious = _previous;
            _previous = current;
        }
    }

    /// <summary>Comment-aware but string-agnostic view: comments act as separators wherever they appear.</summary>
    private sealed class CommentStripper
    {
        private int _blockDepth;

        public void Feed(string line, SqlPatternDetector detector)
        {
            var builder = new StringBuilder(line.Length);
            for (var index = 0; index < line.Length; index++)
            {
                var character = line[index];
                var next = index + 1 < line.Length ? line[index + 1] : '\0';
                if (_blockDepth > 0)
                {
                    if (character == '/' && next == '*')
                    {
                        _blockDepth++;
                        index++;
                    }
                    else if (character == '*' && next == '/')
                    {
                        _blockDepth--;
                        index++;
                    }

                    continue;
                }

                if (character == '-' && next == '-') break;
                if (character == '/' && next == '*')
                {
                    _blockDepth = 1;
                    index++;
                    builder.Append(' ');
                    continue;
                }

                builder.Append(character);
            }

            SqlPatternDetector.FeedRaw(builder.ToString(), detector);
        }
    }

    /// <summary>
    /// A PostgreSQL lexer that tracks string literals (standard and E'' escape strings), quoted identifiers,
    /// dollar quotes and comments across lines, and emits only code tokens.
    /// </summary>
    private sealed class SqlLexer
    {
        private State _state;
        private int _blockDepth;
        private string _dollarTag = string.Empty;

        private enum State
        {
            Code,
            SingleQuoted,
            EscapeQuoted,
            DoubleQuoted,
            DollarQuoted,
            BlockComment
        }

        public bool IsTopLevel => _state == State.Code;

        public void Feed(string line, SqlPatternDetector detector)
        {
            var code = new StringBuilder(line.Length);
            var index = 0;
            while (index < line.Length)
            {
                var character = line[index];
                var next = index + 1 < line.Length ? line[index + 1] : '\0';
                switch (_state)
                {
                    case State.Code:
                        if (character == '-' && next == '-')
                        {
                            index = line.Length;
                            continue;
                        }

                        if (character == '/' && next == '*')
                        {
                            _state = State.BlockComment;
                            _blockDepth = 1;
                            code.Append(' ');
                            index += 2;
                            continue;
                        }

                        if (character == '\'')
                        {
                            var escape = index > 0 && line[index - 1] is 'e' or 'E' &&
                                         (index < 2 || !IsWordCharacter(line[index - 2]));
                            _state = escape ? State.EscapeQuoted : State.SingleQuoted;
                            code.Append(" '' ");
                            index++;
                            continue;
                        }

                        if (character == '"')
                        {
                            _state = State.DoubleQuoted;
                            code.Append(" \"\" ");
                            index++;
                            continue;
                        }

                        if (character == '$' && TryReadDollarTag(line, index, out var tag) &&
                            (index == 0 || !IsWordCharacter(line[index - 1])))
                        {
                            _state = State.DollarQuoted;
                            _dollarTag = tag;
                            code.Append(" $$ ");
                            index += tag.Length;
                            continue;
                        }

                        code.Append(character);
                        index++;
                        continue;
                    case State.SingleQuoted:
                    case State.EscapeQuoted:
                        if (_state == State.EscapeQuoted && character == '\\')
                        {
                            index += 2;
                            continue;
                        }

                        if (character == '\'')
                        {
                            if (next == '\'')
                            {
                                index += 2;
                                continue;
                            }

                            _state = State.Code;
                        }

                        index++;
                        continue;
                    case State.DoubleQuoted:
                        if (character == '"')
                        {
                            if (next == '"')
                            {
                                index += 2;
                                continue;
                            }

                            _state = State.Code;
                        }

                        index++;
                        continue;
                    case State.DollarQuoted:
                        if (character == '$' &&
                            string.CompareOrdinal(line, index, _dollarTag, 0, _dollarTag.Length) == 0)
                        {
                            _state = State.Code;
                            index += _dollarTag.Length;
                            continue;
                        }

                        index++;
                        continue;
                    case State.BlockComment:
                        if (character == '/' && next == '*')
                        {
                            _blockDepth++;
                            index += 2;
                            continue;
                        }

                        if (character == '*' && next == '/')
                        {
                            _blockDepth--;
                            index += 2;
                            if (_blockDepth == 0) _state = State.Code;
                            continue;
                        }

                        index++;
                        continue;
                    default:
                        index++;
                        continue;
                }
            }

            SqlPatternDetector.FeedRaw(code.ToString(), detector);
        }

        private static bool TryReadDollarTag(string line, int start, out string tag)
        {
            tag = string.Empty;
            var index = start + 1;
            if (index < line.Length && line[index] == '$')
            {
                tag = "$$";
                return true;
            }

            if (index >= line.Length || !(char.IsLetter(line[index]) || line[index] == '_')) return false;
            while (index < line.Length && IsWordCharacter(line[index])) index++;
            if (index >= line.Length || line[index] != '$') return false;
            tag = line[start..(index + 1)];
            return true;
        }
    }

    private static bool IsWordCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character == '_';
    }
}
