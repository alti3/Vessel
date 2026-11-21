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

    // At least two labels; labels may contain underscores (upstream v4.2.0 #10663); the TLD is alphabetic or punycode
    // (IDN TLDs such as xn--p1ai). Single-label names are rejected.
    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?\.)+([a-z]{2,63}|xn--[a-z0-9-]{1,59})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DomainNamePattern();
}
