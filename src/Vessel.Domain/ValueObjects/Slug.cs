using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

public readonly partial record struct Slug
{
    public const int MaxLength = 80;

    public Slug(string value)
    {
        var normalized = DomainValidation.Required(value, nameof(Slug), MaxLength).ToLowerInvariant();
        if (!SlugPattern().IsMatch(normalized))
            throw new DomainException("Slug can contain lowercase letters, numbers, and hyphens.");

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex SlugPattern();
}
