namespace Vessel.Infrastructure.Notifications;

internal sealed record EmailNotificationSecret(
    string SmtpHost,
    int SmtpPort,
    string From,
    string To,
    string? Username,
    string? Password,
    bool EnableSsl = true);

internal sealed record WebhookNotificationSecret(string Url, string? Secret);

internal sealed record WebhookUrlSecret(string WebhookUrl);

internal sealed record TelegramNotificationSecret(string BotToken, string ChatId, string? ThreadId);
