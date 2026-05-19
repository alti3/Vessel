using Vessel.Domain.Common;

namespace Vessel.Domain.Webhooks;

public sealed class ApplicationWebhookConfiguration : Entity<ApplicationWebhookConfigurationId>
{
    private ApplicationWebhookConfiguration()
    {
    }

    private ApplicationWebhookConfiguration(
        ApplicationWebhookConfigurationId id,
        ApplicationId applicationId,
        WebhookProvider provider,
        SecretReferenceId secretReferenceId,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        ApplicationId = applicationId;
        Provider = provider;
        SecretReferenceId = secretReferenceId;
        IsEnabled = true;
    }

    public ApplicationId ApplicationId { get; private set; }

    public WebhookProvider Provider { get; private set; }

    public SecretReferenceId SecretReferenceId { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTimeOffset? LastRotatedAt { get; private set; }

    public static ApplicationWebhookConfiguration Create(
        ApplicationId applicationId,
        WebhookProvider provider,
        SecretReferenceId secretReferenceId,
        DateTimeOffset now)
    {
        return new ApplicationWebhookConfiguration(
            ApplicationWebhookConfigurationId.New(),
            applicationId,
            provider,
            secretReferenceId,
            now);
    }

    public void ReplaceSecret(SecretReferenceId secretReferenceId, DateTimeOffset now)
    {
        SecretReferenceId = secretReferenceId;
        LastRotatedAt = now;
        IsEnabled = true;
        Touch(now);
    }

    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        IsEnabled = enabled;
        Touch(now);
    }
}
