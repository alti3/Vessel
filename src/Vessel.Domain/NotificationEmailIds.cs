using Vessel.Domain.Common;

namespace Vessel.Domain;

public readonly record struct NotificationThrottleId(Guid Value) : IStronglyTypedId
{
    public static NotificationThrottleId New()
    {
        return new NotificationThrottleId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct InstanceEmailSettingsId(Guid Value) : IStronglyTypedId
{
    /// <summary>Instance email settings are a singleton; this is the id of the one row.</summary>
    public static readonly InstanceEmailSettingsId Singleton = new(new Guid("00000000-0000-0000-0000-00000000e3a1"));

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
