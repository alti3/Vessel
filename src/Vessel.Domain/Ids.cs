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

public readonly record struct ServiceResourceId(Guid Value) : IStronglyTypedId
{
    public static ServiceResourceId New()
    {
        return new ServiceResourceId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct BackupScheduleId(Guid Value) : IStronglyTypedId
{
    public static BackupScheduleId New()
    {
        return new BackupScheduleId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct BackupExecutionId(Guid Value) : IStronglyTypedId
{
    public static BackupExecutionId New()
    {
        return new BackupExecutionId(Guid.NewGuid());
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

public readonly record struct WebhookEventId(Guid Value) : IStronglyTypedId
{
    public static WebhookEventId New()
    {
        return new WebhookEventId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct ApplicationWebhookConfigurationId(Guid Value) : IStronglyTypedId
{
    public static ApplicationWebhookConfigurationId New()
    {
        return new ApplicationWebhookConfigurationId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct ApplicationPreviewId(Guid Value) : IStronglyTypedId
{
    public static ApplicationPreviewId New()
    {
        return new ApplicationPreviewId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct ProxyConfigurationVersionId(Guid Value) : IStronglyTypedId
{
    public static ProxyConfigurationVersionId New()
    {
        return new ProxyConfigurationVersionId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct CertificateId(Guid Value) : IStronglyTypedId
{
    public static CertificateId New()
    {
        return new CertificateId(Guid.NewGuid());
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

public readonly record struct SecretValueId(Guid Value) : IStronglyTypedId
{
    public static SecretValueId New()
    {
        return new SecretValueId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct EnvironmentVariableId(Guid Value) : IStronglyTypedId
{
    public static EnvironmentVariableId New()
    {
        return new EnvironmentVariableId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct RegistryCredentialId(Guid Value) : IStronglyTypedId
{
    public static RegistryCredentialId New()
    {
        return new RegistryCredentialId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct ServerStatusSnapshotId(Guid Value) : IStronglyTypedId
{
    public static ServerStatusSnapshotId New()
    {
        return new ServerStatusSnapshotId(Guid.NewGuid());
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

public readonly record struct PersonalAccessTokenId(Guid Value) : IStronglyTypedId
{
    public static PersonalAccessTokenId New()
    {
        return new PersonalAccessTokenId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct TeamInvitationId(Guid Value) : IStronglyTypedId
{
    public static TeamInvitationId New()
    {
        return new TeamInvitationId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
