using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

public readonly partial record struct ResourceName
{
    public const int MaxLength = 120;

    public ResourceName(string value)
    {
        var normalized = DomainValidation.Required(value, nameof(ResourceName), MaxLength);
        if (!ResourceNamePattern().IsMatch(normalized))
            throw new DomainException(
                "Resource name can contain letters, numbers, spaces, dots, underscores, and hyphens.");

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._-]*$")]
    private static partial Regex ResourceNamePattern();
}
