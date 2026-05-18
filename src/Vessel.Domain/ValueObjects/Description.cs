using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

public readonly record struct Description
{
    public const int MaxLength = 1000;

    public Description(string? value)
    {
        Value = DomainValidation.Optional(value, nameof(Description), MaxLength);
    }

    public string? Value { get; }

    public override string ToString()
    {
        return Value ?? string.Empty;
    }
}
