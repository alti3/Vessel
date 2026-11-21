using System.Text;

namespace Vessel.Application.ManagedServices;

/// <summary>
///     Writes the <c>.env</c> file that Vessel passes to Docker/Podman Compose with <c>--env-file</c>.
/// </summary>
/// <remarks>
///     Compose applies <c>${VAR}</c> interpolation to unquoted and double-quoted <c>.env</c> values, so a password such as
///     <c>pa$word</c> would silently become <c>pa</c> plus the value of <c>$word</c>. Every value is therefore emitted
///     double-quoted with <c>$</c> escaped as <c>$$</c>, <c>\</c> and <c>"</c> backslash-escaped and line breaks
///     written as <c>\n</c>/<c>\r</c>, which the Compose dotenv parser turns back into the exact original value.
///     Single quotes are not used because Compose keeps a backslash that escapes a single quote inside single quotes.
///     A value that ends with a backslash is refused, because Compose would read the closing quote as escaped.
/// </remarks>
public static class ComposeEnvironmentFile
{
    public static string Format(IReadOnlyDictionary<string, string> values)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append(ServiceTemplateCatalog.EnvironmentKey(key)).Append('=').Append(Quote(value)).Append('\n');
        return builder.ToString();
    }

    public static string Quote(string value)
    {
        // The Compose dotenv parser treats any quote preceded by a backslash as escaped, so an encoded value ending in
        // "\\" would swallow the closing quote. Refuse instead of writing a value Compose would read differently.
        if (value.EndsWith('\\'))
            throw new ArgumentException(
                "Values that end with a backslash cannot be passed to Compose safely; choose a different value.",
                nameof(value));
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
            switch (character)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '$':
                    builder.Append("$$");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                default:
                    builder.Append(character);
                    break;
            }

        return builder.Append('"').ToString();
    }
}
