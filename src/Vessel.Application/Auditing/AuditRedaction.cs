using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Vessel.Application.Auditing;

/// <summary>
///     Redaction of audit metadata and change sets (IAM-08, Coolify <c>RedactSensitiveAuditData</c> parity). Redaction
///     works by key and by value:
///     <list type="bullet">
///         <item>Credential-like keys (password, secret, token, key, credential, cookie, ...) keep no string value.</item>
///         <item>Content keys (command, compose, env, log, output, script, payload, ...) are omitted entirely.</item>
///         <item>Email addresses are masked (<c>j***@example.com</c>), wherever they appear.</item>
///         <item>
///             Every string value is scanned: URL credentials are stripped and bearer tokens, private keys, JWTs and
///             well-known token formats are replaced. Long strings are truncated.
///         </item>
///     </list>
///     Identifiers, counts, flags and timestamps pass through unchanged.
/// </summary>
public static partial class AuditRedaction
{
    public const string Redacted = "[redacted]";
    public const string Omitted = "[omitted]";
    public const string Changed = "[changed]";
    public const int MaxStringLength = 1024;
    private const int MaxDepth = 4;

    private static readonly HashSet<string> SensitiveWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "pwd", "passphrase", "secret", "secrets", "token", "tokens", "apikey", "key", "keys",
        "credential", "credentials", "private", "privatekey", "authorization", "cookie", "cookies", "otp",
        "totp", "recovery", "signature", "hash", "salt", "bearer", "jwt"
    };

    private static readonly HashSet<string> ContentWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "command", "commands", "compose", "dockerfile", "script", "scripts", "log", "logs", "output", "stdout",
        "stderr", "env", "dotenv", "body", "payload", "content", "contents", "certificate", "pem"
    };

    private static readonly HashSet<string> PersonalWords = new(StringComparer.OrdinalIgnoreCase) { "email", "mail" };

    // A trailing descriptor makes a key an identifier or label, not the secret itself (tokenId, sshKeyName, ...).
    private static readonly HashSet<string> DescriptorWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "ids", "count", "name", "names", "type", "types", "fingerprint", "version", "at", "enabled", "set",
        "length", "status", "scope", "scopes", "kind", "provider", "configured", "required"
    };

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public static Dictionary<string, object?> RedactMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata is null) return result;
        foreach ((var key, var value) in metadata) result[key] = RedactValue(key, value, 0);
        return result;
    }

    /// <summary>
    ///     Converts a change list into <c>{ field: { before, after } }</c>. Sensitive or content fields are recorded as
    ///     <see cref="Changed" /> without either value.
    /// </summary>
    public static Dictionary<string, object?>? RedactChanges(IReadOnlyList<AuditFieldChange>? changes)
    {
        if (changes is null || changes.Count == 0) return null;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (AuditFieldChange change in changes)
        {
            KeyClass keyClass = Classify(change.Field);
            result[change.Field] = keyClass is KeyClass.Sensitive or KeyClass.Content
                ? new Dictionary<string, object?> { ["changed"] = true, ["before"] = Changed, ["after"] = Changed }
                : new Dictionary<string, object?>
                {
                    ["before"] = RedactValue(change.Field, change.Before, 0),
                    ["after"] = RedactValue(change.Field, change.After, 0)
                };
        }

        return result;
    }

    /// <summary>Masks an email address as <c>j***@example.com</c>; other values are returned unchanged.</summary>
    public static string MaskEmail(string value)
    {
        var at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1) return value;
        return value[0] + "***" + value[at..];
    }

    /// <summary>Applies the value rules (URL credentials, tokens, emails, truncation) to free text.</summary>
    public static string RedactText(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        try
        {
            var text = PrivateKeyBlock().Replace(value, Redacted);
            text = UrlCredentials().Replace(text, "${scheme}" + Redacted + "@");
            text = BearerToken().Replace(text, "Bearer " + Redacted);
            text = JsonWebToken().Replace(text, Redacted);
            text = KnownTokenFormats().Replace(text, Redacted);
            text = EmailAddress().Replace(text, match => MaskEmail(match.Value));
            return text.Length <= MaxStringLength ? text : text[..MaxStringLength] + "…";
        }
        catch (RegexMatchTimeoutException)
        {
            return Redacted;
        }
    }

    private static object? RedactValue(string key, object? value, int depth)
    {
        if (value is null) return null;
        KeyClass keyClass = Classify(key);

        switch (value)
        {
            case bool or byte or short or int or long or decimal or double or float or Guid or DateTime
                or DateTimeOffset or TimeSpan:
                return value;
            case Enum enumValue:
                return enumValue.ToString();
            case string text:
                return keyClass switch
                {
                    KeyClass.Sensitive => Redacted,
                    KeyClass.Content => Omitted,
                    KeyClass.Personal => MaskEmail(text.Trim()),
                    _ => RedactText(text)
                };
        }

        if (keyClass == KeyClass.Sensitive) return Redacted;
        if (keyClass == KeyClass.Content) return Omitted;
        if (depth >= MaxDepth) return Omitted;

        switch (value)
        {
            case IReadOnlyDictionary<string, object?> nested:
                return nested.ToDictionary(pair => pair.Key, pair => RedactValue(pair.Key, pair.Value, depth + 1),
                    StringComparer.Ordinal);
            case IDictionary dictionary:
                var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    var nestedKey = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
                    copy[nestedKey] = RedactValue(nestedKey, entry.Value, depth + 1);
                }

                return copy;
            case IEnumerable items:
                return items.Cast<object?>().Take(100).Select(item => RedactValue(key, item, depth + 1)).ToArray();
            default:
                return RedactText(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        }
    }

    private static KeyClass Classify(string key)
    {
        List<string> words = SplitWords(key);
        if (words.Count == 0) return KeyClass.Plain;
        if (DescriptorWords.Contains(words[^1]) && words.Count > 1) return KeyClass.Plain;
        if (words.Any(PersonalWords.Contains)) return KeyClass.Personal;
        if (words.Any(SensitiveWords.Contains)) return KeyClass.Sensitive;
        if (words.Any(ContentWords.Contains)) return KeyClass.Content;
        // "connection_string", "database_url" and similar may embed credentials.
        if (words.Count >= 2 && words[^2].Equals("connection", StringComparison.OrdinalIgnoreCase)) return KeyClass.Sensitive;
        return KeyClass.Plain;
    }

    private static List<string> SplitWords(string key)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        for (var index = 0; index < key.Length; index++)
        {
            var character = key[index];
            if (!char.IsLetterOrDigit(character))
            {
                Flush();
                continue;
            }

            if (char.IsUpper(character) && current.Length > 0 && !char.IsUpper(key[index - 1])) Flush();
            current.Append(character);
        }

        Flush();
        return words;

        void Flush()
        {
            if (current.Length == 0) return;
            words.Add(current.ToString());
            current.Clear();
        }
    }

    private enum KeyClass
    {
        Plain,
        Sensitive,
        Content,
        Personal
    }

    [GeneratedRegex("-----BEGIN [A-Z ]*PRIVATE KEY-----[\\s\\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)",
        RegexOptions.CultureInvariant, 250)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"(?<scheme>[a-z][a-z0-9+.\-]*://)[^\s/@:]+(:[^\s/@]*)?@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"Bearer\s+[^\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]*", RegexOptions.CultureInvariant, 250)]
    private static partial Regex JsonWebToken();

    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|gl(pat|cbt|rt)-[A-Za-z0-9\-_]{20,}|A(KIA|SIA)[A-Z0-9]{16}|xox[abposr]-[A-Za-z0-9\-]{10,}|sk_(live|test)_[A-Za-z0-9]{16,})",
        RegexOptions.CultureInvariant, 250)]
    private static partial Regex KnownTokenFormats();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, 250)]
    private static partial Regex EmailAddress();
}
