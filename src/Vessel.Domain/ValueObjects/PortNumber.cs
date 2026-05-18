using System.Globalization;
using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

public readonly record struct PortNumber
{
    public PortNumber(int value)
    {
        if (value is < 1 or > 65535) throw new DomainException("Port must be between 1 and 65535.");

        Value = value;
    }

    public int Value { get; }

    public override string ToString()
    {
        return Value.ToString(CultureInfo.InvariantCulture);
    }
}
