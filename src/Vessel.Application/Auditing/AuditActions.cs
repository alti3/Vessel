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
}
