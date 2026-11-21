using Vessel.Domain.Common;

namespace Vessel.Domain;

/// <summary>A server-side interactive session (IAM-09).</summary>
public readonly record struct UserSessionId(Guid Value) : IStronglyTypedId
{
    public static UserSessionId New()
    {
        return new UserSessionId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>A configured external sign-in provider instance (IAM-07).</summary>
public readonly record struct AuthProviderId(Guid Value) : IStronglyTypedId
{
    public static AuthProviderId New()
    {
        return new AuthProviderId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>A linked external identity (IAM-07).</summary>
public readonly record struct OauthIdentityId(Guid Value) : IStronglyTypedId
{
    public static OauthIdentityId New()
    {
        return new OauthIdentityId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
