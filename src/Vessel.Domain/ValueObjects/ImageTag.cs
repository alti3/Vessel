using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

public readonly partial record struct ImageTag
{
    public const int MaxLength = 128;

    public ImageTag(string value)
    {
        var normalized = DomainValidation.Required(value, nameof(ImageTag), MaxLength);
        if (!ImageTagPattern().IsMatch(normalized)) throw new DomainException("Container image tag is invalid.");

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex ImageTagPattern();
}
