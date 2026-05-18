using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

public readonly record struct DisplayName
{
    public const int MaxLength = 120;

    public DisplayName(string value)
    {
        Value = DomainValidation.Required(value, nameof(DisplayName), MaxLength);
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
