using Vessel.Domain.Common;

// Kept beside the routing entities (instead of Ids.cs) so parallel feature work does not collide in one file.
// ReSharper disable once CheckNamespace
namespace Vessel.Domain;

public readonly record struct ProxyDynamicConfigurationId(Guid Value) : IStronglyTypedId
{
    public static ProxyDynamicConfigurationId New()
    {
        return new ProxyDynamicConfigurationId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct ServiceDomainId(Guid Value) : IStronglyTypedId
{
    public static ServiceDomainId New()
    {
        return new ServiceDomainId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
