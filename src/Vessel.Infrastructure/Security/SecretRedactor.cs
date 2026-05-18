using System.Text;
using System.Text.RegularExpressions;
using Vessel.Application.Security;

namespace Vessel.Infrastructure.Security;

public sealed class SecretRedactor : ISecretRedactor
{
    private const string Replacement = "<REDACTED>";

    private static readonly IReadOnlyDictionary<string, Regex> NamedPatterns = new Dictionary<string, Regex>
    {
        ["uri_credentials"] = new(@"(?<=://)[^/\s:@]+:[^@\s/]+@", RegexOptions.Compiled),
        ["github_token"] = new(@"gh[pousr]_[A-Za-z0-9_]{20,}", RegexOptions.Compiled),
        ["bearer_token"] = new(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{16,}", RegexOptions.Compiled),
        ["assignment_secret"] = new(@"(?i)\b(password|token|secret|api[_-]?key)=([^\s]+)", RegexOptions.Compiled)
    };

    public string Redact(string value, RedactionContext? context = null)
    {
        if (string.IsNullOrEmpty(value)) return value;

        string redacted = value;
        foreach (string secret in context?.SecretValues ?? [])
        {
            if (!string.IsNullOrWhiteSpace(secret))
                redacted = redacted.Replace(secret, Replacement, StringComparison.Ordinal);
        }

        IEnumerable<string> patternNames = context?.PatternNames is { Count: > 0 }
            ? context.PatternNames
            : NamedPatterns.Keys;

        foreach (string patternName in patternNames)
        {
            if (!NamedPatterns.TryGetValue(patternName, out Regex? pattern)) continue;
            redacted = patternName == "assignment_secret"
                ? pattern.Replace(redacted, "$1=<REDACTED>")
                : pattern.Replace(redacted, Replacement);
        }

        return redacted;
    }

    public byte[] RedactUtf8(byte[] value, RedactionContext? context = null)
    {
        string text = Encoding.UTF8.GetString(value);
        return Encoding.UTF8.GetBytes(Redact(text, context));
    }
}
