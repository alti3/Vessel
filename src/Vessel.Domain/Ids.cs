using Vessel.Domain.Common;

namespace Vessel.Domain;

public readonly record struct UserId(Guid Value) : IStronglyTypedId
{
    public static UserId New()
    {
        return new UserId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct TeamId(Guid Value) : IStronglyTypedId
{
    public static TeamId New()
    {
        return new TeamId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct ProjectId(Guid Value) : IStronglyTypedId
{
    public static ProjectId New()
    {
        return new ProjectId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct EnvironmentId(Guid Value) : IStronglyTypedId
{
    public static EnvironmentId New()
    {
        return new EnvironmentId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct ServerId(Guid Value) : IStronglyTypedId
{
    public static ServerId New()
    {
        return new ServerId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct ApplicationId(Guid Value) : IStronglyTypedId
{
    public static ApplicationId New()
    {
        return new ApplicationId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct DatabaseResourceId(Guid Value) : IStronglyTypedId
{
    public static DatabaseResourceId New()
    {
        return new DatabaseResourceId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct DeploymentId(Guid Value) : IStronglyTypedId
{
    public static DeploymentId New()
    {
        return new DeploymentId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct SecretReferenceId(Guid Value) : IStronglyTypedId
{
    public static SecretReferenceId New()
    {
        return new SecretReferenceId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct NotificationTargetId(Guid Value) : IStronglyTypedId
{
    public static NotificationTargetId New()
    {
        return new NotificationTargetId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct AuditLogId(Guid Value) : IStronglyTypedId
{
    public static AuditLogId New()
    {
        return new AuditLogId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct SettingId(Guid Value) : IStronglyTypedId
{
    public static SettingId New()
    {
        return new SettingId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
