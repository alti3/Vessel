namespace Vessel.Application.Auditing;

public static class AuditActions
{
    public const string UserRegistered = "auth.user_registered";
    public const string UserLoggedIn = "auth.login";
    public const string UserLoginFailed = "auth.login_failed";
    public const string UserLoggedOut = "auth.logout";
    public const string PasswordResetRequested = "auth.password_reset_requested";
    public const string PasswordResetCompleted = "auth.password_reset_completed";
    public const string TwoFactorEnabled = "auth.two_factor_enabled";
    public const string TwoFactorDisabled = "auth.two_factor_disabled";
    public const string TokenCreated = "auth.token_created";
    public const string TokenRevoked = "auth.token_revoked";
    public const string TeamInvitationCreated = "team.invitation_created";
    public const string TeamInvitationAccepted = "team.invitation_accepted";
    public const string TeamMemberRemoved = "team.member_removed";
    public const string TeamMemberRoleChanged = "team.member_role_changed";
    public const string ProjectCreated = "project.created";
    public const string ProjectUpdated = "project.updated";
    public const string ProjectArchived = "project.archived";
    public const string EnvironmentCreated = "environment.created";
    public const string EnvironmentUpdated = "environment.updated";
    public const string EnvironmentDeleted = "environment.deleted";
    public const string ServerCreated = "server.created";
    public const string ServerConnectivityChecked = "server.connectivity_checked";
    public const string ApplicationCreated = "application.created";
    public const string DeploymentStarted = "deployment.started";
    public const string DeploymentCanceled = "deployment.canceled";
    public const string DeploymentFinished = "deployment.finished";
    public const string WebhookConfigured = "webhook.configured";
    public const string WebhookReceived = "webhook.received";
    public const string WebhookRejected = "webhook.rejected";
    public const string WebhookProcessed = "webhook.processed";
    public const string PreviewOpened = "preview.opened";
    public const string PreviewArchived = "preview.archived";
    public const string DomainRouteConfigured = "domain_route.configured";
    public const string DomainRouteRemoved = "domain_route.removed";
    public const string ProxyConfigurationApplied = "proxy_configuration.applied";
    public const string ProxyConfigurationRolledBack = "proxy_configuration.rolled_back";
    public const string CertificateIssuanceQueued = "certificate.issuance_queued";
    public const string DatabaseCreated = "database.created";
    public const string EnvironmentVariableCreated = "environment_variable.created";
    public const string SecretStored = "secret.stored";
    public const string SecretRevealed = "secret.revealed";
    public const string SecretRotated = "secret.rotated";
    public const string RegistryCredentialCreated = "registry_credential.created";
}
