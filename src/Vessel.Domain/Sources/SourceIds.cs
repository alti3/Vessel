using Vessel.Domain.Common;

namespace Vessel.Domain;

public readonly record struct GitSourceConnectionId(Guid Value) : IStronglyTypedId
{
    public static GitSourceConnectionId New()
    {
        return new GitSourceConnectionId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct GitKnownHostId(Guid Value) : IStronglyTypedId
{
    public static GitKnownHostId New()
    {
        return new GitKnownHostId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
