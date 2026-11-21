using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.EnvironmentVariables;

/// <summary>
///     An environment variable name. Upstream Coolify (v4.4.0) accepts <c>[A-Za-z_][A-Za-z0-9_.]*</c>: dots are allowed
///     because runtimes such as Spring read dotted names from the container environment. Dotted names are not valid shell
///     names, so they cannot be passed to an image build (<see cref="IsShellSafe" />); deployments skip them for the build
///     with a warning and still pass them to the container.
/// </summary>
public readonly partial record struct EnvironmentVariableKey
{
    public const int MaxLength = 160;

    public EnvironmentVariableKey(string value)
    {
        var normalized = DomainValidation.Required(value, nameof(EnvironmentVariableKey), MaxLength).Trim();
        if (!KeyPattern().IsMatch(normalized))
            throw new DomainException(
                "Environment variable keys must start with a letter or underscore and contain only letters, numbers, underscores, and dots.");

        Value = normalized;
    }

    public string Value { get; }

    /// <summary>True when the name is a POSIX shell name (no dots) and can be passed to a build.</summary>
    public bool IsShellSafe => IsShellName(Value);

    public static bool IsValid(string? value)
    {
        return value is { Length: > 0 and <= MaxLength } && KeyPattern().IsMatch(value);
    }

    public static bool IsShellName(string? value)
    {
        return value is { Length: > 0 and <= MaxLength } && ShellPattern().IsMatch(value);
    }

    public override string ToString()
    {
        return Value;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ShellPattern();
}
