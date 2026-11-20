namespace Vessel.Infrastructure.Notifications;

/// <summary>
///     Email target credentials. With <see cref="UseSystemSettings" /> only <see cref="To" /> is used and mail goes
///     through the instance email settings. <see cref="Encryption" /> is <c>starttls</c>, <c>tls</c> (implicit, 465),
///     or <c>none</c>; when absent, the legacy <see cref="EnableSsl" /> flag decides.
/// </summary>
internal sealed record EmailNotificationSecret(
    string? SmtpHost,
    int? SmtpPort,
    string? From,
    string? To,
    string? Username,
    string? Password,
    bool EnableSsl = true,
    string? Encryption = null,
    string? FromName = null,
    string? EhloDomain = null,
    int? TimeoutSeconds = null,
    bool UseSystemSettings = false)
{
    public override string ToString()
    {
        return "EmailNotificationSecret { [redacted] }";
    }
}

internal sealed record WebhookNotificationSecret(string Url, string? Secret);

internal sealed record WebhookUrlSecret(string WebhookUrl);

internal sealed record TelegramNotificationSecret(string BotToken, string ChatId, string? ThreadId);
