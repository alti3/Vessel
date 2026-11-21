using System.Text;

namespace Vessel.Application.EnvironmentVariables;

/// <summary>
///     Writes the Compose env file (<c>env_file</c> / <c>--env-file</c>) so that every value reaches the container
///     exactly (DEP-10). Values are double-quoted with <c>\</c>, <c>"</c> and <c>$</c> escaped (upstream
///     <c>escapeComposeEnvFileValue</c> without interpolation): Vessel already interpolated them, so Compose never
///     expands anything and never reads the control plane's process environment. Line breaks stay raw inside the quotes,
///     so multiline values keep their trailing newline. Preserved Compose nulls are left out (unset). Variables that still
///     use legacy escaping are written exactly as Vessel wrote them before (<see cref="LegacyValue" />).
/// </summary>
public static class ComposeEnvFileWriter
{
    public static string Write(IEnumerable<ResolvedVariable> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var builder = new StringBuilder();
        foreach (ResolvedVariable variable in variables)
        {
            if (variable.Value is null) continue;
            builder.Append(variable.Key).Append('=');
            builder.Append(variable.UsesLegacyEscaping ? LegacyValue(variable.RawValue ?? variable.Value) : Exact(variable.Value));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>An exact (non-interpolated) double-quoted dotenv value.</summary>
    public static string Exact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    ///     The quoting Vessel used before exact values: bare unless the value has whitespace, <c>"</c> or <c>#</c>; then
    ///     double-quoted with <c>\</c> and <c>"</c> escaped. Compose interpolates <c>$</c> in these values, as before.
    /// </summary>
    public static string LegacyValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0) return "\"\"";
        if (value.Any(char.IsWhiteSpace) || value.Contains('"', StringComparison.Ordinal) ||
            value.Contains('#', StringComparison.Ordinal))
            return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        return value;
    }
}

/// <summary>
///     Writes a POSIX shell file of <c>KEY='value'</c> assignments (single quotes, <c>'</c> as <c>'\''</c>), so that
///     sourcing it sets every value exactly and expands nothing. Used to hand build values to the runtime CLI on an SSH
///     server without putting them on a command line. Keys must be shell names.
/// </summary>
public static class ShellEnvFileWriter
{
    public static string Write(IEnumerable<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var builder = new StringBuilder();
        foreach (var (key, value) in values)
        {
            if (!Domain.EnvironmentVariables.EnvironmentVariableKey.IsShellName(key))
                throw new ArgumentException($"'{key}' is not a shell variable name.", nameof(values));
            builder.Append(key).Append('=').Append(Quote(value)).Append('\n');
        }

        return builder.ToString();
    }

    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
