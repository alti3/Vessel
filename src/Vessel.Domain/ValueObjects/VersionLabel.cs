using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

public readonly record struct VersionLabel
{
    public const int MaxLength = 80;

    public VersionLabel(string value)
    {
        Value = DomainValidation.Required(value, nameof(VersionLabel), MaxLength);
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
