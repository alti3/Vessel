using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vessel.Domain;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Infrastructure.Persistence;

internal static class StronglyTypedIdConversions
{
    public static readonly ValueConverter<UserId, Guid> UserId = new(id => id.Value, value => new UserId(value));
    public static readonly ValueConverter<TeamId, Guid> TeamId = new(id => id.Value, value => new TeamId(value));

    public static readonly ValueConverter<ProjectId, Guid> ProjectId = new(id => id.Value,
        value => new ProjectId(value));

    public static readonly ValueConverter<EnvironmentId, Guid> EnvironmentId = new(id => id.Value,
        value => new EnvironmentId(value));

    public static readonly ValueConverter<ServerId, Guid> ServerId = new(id => id.Value, value => new ServerId(value));
    public static readonly ValueConverter<AppId, Guid> ApplicationId = new(id => id.Value, value => new AppId(value));

    public static readonly ValueConverter<DatabaseResourceId, Guid> DatabaseResourceId =
        new(id => id.Value, value => new DatabaseResourceId(value));

    public static readonly ValueConverter<DeploymentId, Guid> DeploymentId = new(id => id.Value,
        value => new DeploymentId(value));

    public static readonly ValueConverter<WebhookEventId, Guid> WebhookEventId = new(id => id.Value,
        value => new WebhookEventId(value));

    public static readonly ValueConverter<ApplicationWebhookConfigurationId, Guid> ApplicationWebhookConfigurationId =
        new(id => id.Value, value => new ApplicationWebhookConfigurationId(value));

    public static readonly ValueConverter<ApplicationPreviewId, Guid> ApplicationPreviewId =
        new(id => id.Value, value => new ApplicationPreviewId(value));

    public static readonly ValueConverter<ProxyConfigurationVersionId, Guid> ProxyConfigurationVersionId =
        new(id => id.Value, value => new ProxyConfigurationVersionId(value));

    public static readonly ValueConverter<CertificateId, Guid> CertificateId =
        new(id => id.Value, value => new CertificateId(value));

    public static readonly ValueConverter<SecretReferenceId, Guid> SecretReferenceId =
        new(id => id.Value, value => new SecretReferenceId(value));

    public static readonly ValueConverter<SecretValueId, Guid> SecretValueId =
        new(id => id.Value, value => new SecretValueId(value));

    public static readonly ValueConverter<EnvironmentVariableId, Guid> EnvironmentVariableId =
        new(id => id.Value, value => new EnvironmentVariableId(value));

    public static readonly ValueConverter<RegistryCredentialId, Guid> RegistryCredentialId =
        new(id => id.Value, value => new RegistryCredentialId(value));

    public static readonly ValueConverter<ServerStatusSnapshotId, Guid> ServerStatusSnapshotId =
        new(id => id.Value, value => new ServerStatusSnapshotId(value));

    public static readonly ValueConverter<NotificationTargetId, Guid> NotificationTargetId =
        new(id => id.Value, value => new NotificationTargetId(value));

    public static readonly ValueConverter<AuditLogId, Guid> AuditLogId = new(id => id.Value,
        value => new AuditLogId(value));

    public static readonly ValueConverter<SettingId, Guid> SettingId = new(id => id.Value,
        value => new SettingId(value));

    public static readonly ValueConverter<PersonalAccessTokenId, Guid> PersonalAccessTokenId =
        new(id => id.Value, value => new PersonalAccessTokenId(value));

    public static readonly ValueConverter<TeamInvitationId, Guid> TeamInvitationId =
        new(id => id.Value, value => new TeamInvitationId(value));

    public static PropertyBuilder<UserId> HasUserIdConversion(this PropertyBuilder<UserId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(UserId);
    }

    public static PropertyBuilder<TeamId> HasTeamIdConversion(this PropertyBuilder<TeamId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(TeamId);
    }

    public static PropertyBuilder<ProjectId> HasProjectIdConversion(this PropertyBuilder<ProjectId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(ProjectId);
    }

    public static PropertyBuilder<EnvironmentId> HasEnvironmentIdConversion(
        this PropertyBuilder<EnvironmentId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(EnvironmentId);
    }

    public static PropertyBuilder<ServerId> HasServerIdConversion(this PropertyBuilder<ServerId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(ServerId);
    }

    public static PropertyBuilder<AppId> HasApplicationIdConversion(this PropertyBuilder<AppId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(ApplicationId);
    }

    public static PropertyBuilder<DatabaseResourceId> HasDatabaseResourceIdConversion(
        this PropertyBuilder<DatabaseResourceId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(DatabaseResourceId);
    }

    public static PropertyBuilder<DeploymentId> HasDeploymentIdConversion(
        this PropertyBuilder<DeploymentId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(DeploymentId);
    }

    public static PropertyBuilder<WebhookEventId> HasWebhookEventIdConversion(
        this PropertyBuilder<WebhookEventId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(WebhookEventId);
    }

    public static PropertyBuilder<ApplicationWebhookConfigurationId> HasApplicationWebhookConfigurationIdConversion(
        this PropertyBuilder<ApplicationWebhookConfigurationId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(ApplicationWebhookConfigurationId);
    }

    public static PropertyBuilder<ApplicationPreviewId> HasApplicationPreviewIdConversion(
        this PropertyBuilder<ApplicationPreviewId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(ApplicationPreviewId);
    }

    public static PropertyBuilder<ProxyConfigurationVersionId> HasProxyConfigurationVersionIdConversion(
        this PropertyBuilder<ProxyConfigurationVersionId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(ProxyConfigurationVersionId);
    }

    public static PropertyBuilder<CertificateId> HasCertificateIdConversion(
        this PropertyBuilder<CertificateId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(CertificateId);
    }

    public static PropertyBuilder<SecretReferenceId> HasSecretReferenceIdConversion(
        this PropertyBuilder<SecretReferenceId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(SecretReferenceId);
    }

    public static PropertyBuilder<SecretValueId> HasSecretValueIdConversion(
        this PropertyBuilder<SecretValueId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(SecretValueId);
    }

    public static PropertyBuilder<EnvironmentVariableId> HasEnvironmentVariableIdConversion(
        this PropertyBuilder<EnvironmentVariableId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(EnvironmentVariableId);
    }

    public static PropertyBuilder<RegistryCredentialId> HasRegistryCredentialIdConversion(
        this PropertyBuilder<RegistryCredentialId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(RegistryCredentialId);
    }

    public static PropertyBuilder<ServerStatusSnapshotId> HasServerStatusSnapshotIdConversion(
        this PropertyBuilder<ServerStatusSnapshotId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(ServerStatusSnapshotId);
    }

    public static PropertyBuilder<NotificationTargetId> HasNotificationTargetIdConversion(
        this PropertyBuilder<NotificationTargetId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(NotificationTargetId);
    }

    public static PropertyBuilder<AuditLogId> HasAuditLogIdConversion(this PropertyBuilder<AuditLogId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(AuditLogId);
    }

    public static PropertyBuilder<SettingId> HasSettingIdConversion(this PropertyBuilder<SettingId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(SettingId);
    }

    public static PropertyBuilder<PersonalAccessTokenId> HasPersonalAccessTokenIdConversion(
        this PropertyBuilder<PersonalAccessTokenId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(PersonalAccessTokenId);
    }

    public static PropertyBuilder<TeamInvitationId> HasTeamInvitationIdConversion(
        this PropertyBuilder<TeamInvitationId> propertyBuilder)
    {
        return propertyBuilder.HasConversion(TeamInvitationId);
    }
}
