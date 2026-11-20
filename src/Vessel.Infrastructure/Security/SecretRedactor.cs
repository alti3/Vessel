using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vessel.Application.Security;

namespace Vessel.Infrastructure.Security;

/// <summary>
///     Redacts secrets from log and command output with the rule set of Coolify's <c>SensitiveDataRedactor</c>:
///     known and locked values (and their URL-, JSON- and base64-encoded variants, longest first), Authorization
///     headers, <c>x-access-token:</c>/<c>oauth2:</c> URLs, URL credentials scoped to one line, GitHub (including dotted
///     and <c>github_pat_</c>), GitLab and AWS tokens, PEM private key blocks (also unterminated ones) and key-based
///     assignments in env, shell, JSON and YAML form. When a pattern times out the text is redacted line by line and a
///     line that still fails is suppressed, never passed through.
/// </summary>
public sealed class SecretRedactor : ISecretRedactor
{
    public const string Replacement = "<REDACTED>";
    public const string FailureMessage = "[LOG SUPPRESSED: REDACTION FAILED]";
    public const int MinKnownSecretLength = 8;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private const string SensitiveKeys =
        "password|passwd|secret|client_secret|api_key|apikey|access_key|private_key|token|access_token|refresh_token|" +
        "auth_token|cookie|set_cookie|webhook_secret|signing_secret|credential|credentials|connection_string|database_url";

    // A key matches when it is a sensitive name or ends with one, such as smtp_password or github-token.
    private const string AssignmentKeys =
        "(?i:(?:[a-z0-9]+[_-])*(?:" + SensitiveKeys + "))|[A-Z][A-Z0-9_]*(?:SECRET|PASSWORD|TOKEN|PASSWD|API_KEY|PRIVATE_KEY)[A-Z0-9_]*";

    private const string AlreadyRedacted = "(?!<REDACTED>)";

    private static readonly Regex QuotedShellAssignment = new(
        "(?<![\\w-])([\"'])((?:" + AssignmentKeys + ")\\s*=\\s*)" + AlreadyRedacted + "(?:(?!\\1)[^\\r\\n])*\\1",
        RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex Assignment = new(
        "(?<![\\w-])(?<prefix>[\"']?(?<key>" + AssignmentKeys + ")[\"']?\\s*(?<separator>[=:])\\s*)" + AlreadyRedacted +
        "(?<value>\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"|'(?:[^'\\\\\\r\\n]|\\\\.)*'|[^\\s,;}\\]]+)" +
        "(?<end>(?=[^\\S\\r\\n]*(?:$|[\\r\\n,;}\\]])))?",
        RegexOptions.Multiline | RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex AnsiEscape = new(@"\x1B(?:[@-Z\\\-_]|\[[0-?]*[ -/]*[@-~])",
        RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex ControlCharacters = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]",
        RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex PrivateKeyBegin = new("-----BEGIN [A-Z ]*PRIVATE KEY-----",
        RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex PrivateKeyEnd = new("-----END [A-Z ]*PRIVATE KEY-----",
        RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly (string Name, Regex Pattern, string Replacement)[] NamedPatterns =
    [
        ("authorization_header",
            new Regex(@"((?:Authorization|Proxy-Authorization)\s*:\s*)[^\r\n]+",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout), "$1" + Replacement),
        ("access_token_url", new Regex("x-access-token:.*?(?=@)", RegexOptions.CultureInvariant, MatchTimeout),
            "x-access-token:" + Replacement),
        ("access_token_url", new Regex("oauth2:.*?(?=@)", RegexOptions.CultureInvariant, MatchTimeout),
            "oauth2:" + Replacement),
        // User and password cannot contain whitespace, so a match never spans log lines.
        ("uri_credentials",
            new Regex(@"((?:[a-z][a-z0-9+.\-]*):\/\/[^:\s@\/]+:)[^@\s]+(@)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout), "${1}" + Replacement + "${2}"),
        ("email",
            new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.CultureInvariant, MatchTimeout),
            Replacement),
        ("bearer_token", new Regex(@"Bearer\s+[^\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            MatchTimeout), "Bearer " + Replacement),
        ("github_token", new Regex(@"\bgh[pousr]_[A-Za-z0-9.\-_]{20,}(?![A-Za-z0-9.\-_])",
            RegexOptions.CultureInvariant, MatchTimeout), Replacement),
        ("github_token", new Regex(@"\bgithub_pat_[A-Za-z0-9_]{20,}", RegexOptions.CultureInvariant, MatchTimeout),
            Replacement),
        ("gitlab_token", new Regex(@"\bgl(?:pat|cbt|rt)-[A-Za-z0-9\-_]{20,}\b", RegexOptions.CultureInvariant,
            MatchTimeout), Replacement),
        ("aws_access_key", new Regex(@"\bA(?:KIA|BIA|CCA|SIA)[A-Z0-9]{16}\b", RegexOptions.CultureInvariant,
            MatchTimeout), Replacement),
        ("aws_secret_key", new Regex(@"(aws_secret_access_key)[=:]\s*['""]?[A-Za-z0-9/+=]{40}['""]?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout), "$1=" + Replacement),
        ("api_key", new Regex(@"(api[_-]?key|apikey|api[_-]?secret|secret[_-]?key)[=:]\s*['""]?[A-Za-z0-9\-_]{16,}['""]?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout), "$1=" + Replacement),
        ("private_key", new Regex("-----BEGIN [A-Z ]*PRIVATE KEY-----[\\s\\S]*?-----END [A-Z ]*PRIVATE KEY-----",
            RegexOptions.CultureInvariant, MatchTimeout), Replacement)
    ];

    private static readonly JsonSerializerOptions RelaxedJson = new()
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Redact(string value, RedactionContext? context = null)
    {
        if (string.IsNullOrEmpty(value)) return value;
        try
        {
            var text = context?.IsLogOutput == true ? Normalize(value) : value;
            text = ReplaceKnownSecrets(text, context);
            return RedactPatternsAndAssignments(text, context);
        }
        catch (RegexMatchTimeoutException)
        {
            return RedactLineByLine(value, context);
        }
        catch (ArgumentException)
        {
            return FailureMessage;
        }
    }

    public byte[] RedactUtf8(byte[] value, RedactionContext? context = null)
    {
        var text = Encoding.UTF8.GetString(value);
        return Encoding.UTF8.GetBytes(Redact(text, context));
    }

    /// <summary>All named patterns (the default set). <c>email</c> applies only to log output.</summary>
    public static IReadOnlyList<string> PatternNamesInOrder =>
        NamedPatterns.Select(pattern => pattern.Name).Append("assignment_secret").Distinct().ToArray();

    private static string RedactLineByLine(string value, RedactionContext? context)
    {
        var builder = new StringBuilder(value.Length);
        var insidePrivateKey = false;
        foreach (var line in SplitKeepingNewlines(value))
        {
            var newline = line.EndsWith('\n') ? "\n" : string.Empty;
            try
            {
                if (!insidePrivateKey && PrivateKeyBegin.IsMatch(line) && !PrivateKeyEnd.IsMatch(line))
                {
                    insidePrivateKey = true;
                    builder.Append(Replacement).Append(newline);
                    continue;
                }

                if (insidePrivateKey)
                {
                    insidePrivateKey = !PrivateKeyEnd.IsMatch(line);
                    continue;
                }

                var text = context?.IsLogOutput == true ? Normalize(line) : line;
                builder.Append(RedactPatternsAndAssignments(ReplaceKnownSecrets(text, context), context));
            }
            catch (RegexMatchTimeoutException)
            {
                builder.Append(FailureMessage).Append(newline);
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<string> SplitKeepingNewlines(string value)
    {
        var start = 0;
        while (start < value.Length)
        {
            var newline = value.IndexOf('\n', start);
            var end = newline < 0 ? value.Length : newline + 1;
            yield return value[start..end];
            start = end;
        }
    }

    private static string Normalize(string text)
    {
        text = AnsiEscape.Replace(text, string.Empty);
        return ControlCharacters.Replace(text, string.Empty);
    }

    private static string RedactPatternsAndAssignments(string text, RedactionContext? context)
    {
        IReadOnlyList<string>? selected = context?.PatternNames is { Count: > 0 } names ? names : null;
        bool Enabled(string name)
        {
            if (name == "email" && context?.IsLogOutput != true && selected?.Contains("email") != true) return false;
            return selected is null || selected.Contains(name, StringComparer.Ordinal);
        }

        foreach (var (name, pattern, replacement) in NamedPatterns)
            if (Enabled(name))
                text = pattern.Replace(text, replacement);

        if (Enabled("private_key")) text = RedactUnterminatedPrivateKey(text);
        if (Enabled("assignment_secret")) text = RedactAssignments(text);
        return text;
    }

    /// <summary>A private key that never ends (truncated output) is hidden from its BEGIN line to the end.</summary>
    private static string RedactUnterminatedPrivateKey(string text)
    {
        Match begin = PrivateKeyBegin.Match(text);
        if (!begin.Success) return text;
        return text[..begin.Index] + Replacement;
    }

    private static string RedactAssignments(string text)
    {
        // A quote that opens before the key and closes after the value wraps the whole assignment, as in the shell
        // argument 'DB_PASSWORD=a b'. Hide everything up to the closing quote and keep the quote.
        text = QuotedShellAssignment.Replace(text, match =>
            match.Groups[1].Value + match.Groups[2].Value + Replacement + match.Groups[1].Value);

        return Assignment.Replace(text, match =>
        {
            var value = match.Groups["value"].Value;
            var prefix = match.Groups["prefix"].Value;
            var key = match.Groups["key"].Value;
            var quote = value.Length > 1 && (value[0] is '"' or '\'') && value[^1] == value[0]
                ? value[0].ToString()
                : string.Empty;
            // Prose such as "Failed to refresh token: Connection refused" uses a plain word key, a colon and a plain
            // word value.
            var isProse = quote.Length == 0 &&
                          match.Groups["separator"].Value == ":" &&
                          prefix[0] is not ('"' or '\'') &&
                          IsPlainWord(key) &&
                          value.All(char.IsAsciiLetter) &&
                          !match.Groups["end"].Success;
            return isProse ? match.Value : prefix + quote + Replacement + quote;
        });
    }

    private static bool IsPlainWord(string key)
    {
        return key.Length > 0 && char.IsAsciiLetter(key[0]) && key.Skip(1).All(char.IsAsciiLetterLower);
    }

    private static string ReplaceKnownSecrets(string text, RedactionContext? context)
    {
        if (context is null) return text;
        var variants = new HashSet<string>(StringComparer.Ordinal);
        var shortVariants = new HashSet<string>(StringComparer.Ordinal);
        AddVariants(context.KnownValues, MinKnownSecretLength, variants, shortVariants);
        AddVariants(context.SecretValues, 1, variants, shortVariants);

        foreach (var variant in variants.OrderByDescending(item => item.Length))
            text = text.Replace(variant, Replacement, StringComparison.Ordinal);

        if (shortVariants.Count == 0) return text;
        // Short locked values such as "ab" would hit inside ordinary words, so they only match as whole tokens.
        var alternatives = string.Join('|', shortVariants.OrderByDescending(item => item.Length).Select(Regex.Escape));
        return Regex.Replace(text, @"(?<![\p{L}\p{M}\p{N}_])(?:" + alternatives + @")(?![\p{L}\p{M}\p{N}_])",
            Replacement, RegexOptions.CultureInvariant, MatchTimeout);
    }

    private static void AddVariants(IReadOnlyList<string>? values, int minimumLength, HashSet<string> variants,
        HashSet<string> shortVariants)
    {
        if (values is null) return;
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length < minimumLength) continue;
            // Multiline values (PEM keys, certificates) are also matched line by line, because streamed output
            // arrives one line at a time.
            IEnumerable<string> candidates = raw.Contains('\n', StringComparison.Ordinal)
                ? raw.Split('\n').Select(line => line.TrimEnd('\r'))
                    .Where(line => line.Trim().Length >= MinKnownSecretLength).Prepend(raw)
                : [raw];
            foreach (var secret in candidates)
            {
                HashSet<string> target = secret.Length < MinKnownSecretLength ? shortVariants : variants;
                target.Add(secret);
                target.Add(Uri.EscapeDataString(secret));
                target.Add(JsonSerializer.Serialize(secret, RelaxedJson)[1..^1]);
                target.Add(JsonSerializer.Serialize(secret)[1..^1]);
                if (secret.Length >= MinKnownSecretLength)
                    variants.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)));
            }
        }

        variants.Remove(string.Empty);
        shortVariants.Remove(string.Empty);
    }
}
