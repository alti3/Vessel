using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

public readonly partial record struct DomainName
{
    public const int MaxLength = 253;

    public DomainName(string value)
    {
        var normalized = DomainValidation.Required(value, nameof(DomainName), MaxLength).ToLowerInvariant();
        if (!DomainNamePattern().IsMatch(normalized)) throw new DomainException("Domain name is invalid.");

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }

    [GeneratedRegex(@"^(?!-)([a-z0-9-]{1,63}\.)+[a-z]{2,63}$")]
    private static partial Regex DomainNamePattern();
}
