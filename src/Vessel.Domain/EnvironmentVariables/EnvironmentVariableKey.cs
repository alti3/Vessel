using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.EnvironmentVariables;

public readonly partial record struct EnvironmentVariableKey
{
    public const int MaxLength = 160;

    public EnvironmentVariableKey(string value)
    {
        string normalized = DomainValidation.Required(value, nameof(EnvironmentVariableKey), MaxLength).Trim();
        if (!KeyPattern().IsMatch(normalized))
            throw new DomainException("Environment variable keys must start with a letter or underscore and contain only letters, numbers, and underscores.");

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex KeyPattern();
}
