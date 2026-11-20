using System.Net;
using Vessel.Application.Email;
using Vessel.Application.Notifications;
using Vessel.Domain.Notifications;
using Vessel.Domain.Settings;

namespace Vessel.Infrastructure.Notifications;

/// <summary>
///     Email notification channel (NTF-06). A target either carries its own SMTP server in the secret JSON or sets
///     <c>useSystemSettings</c> to send through the instance email settings with the notification sender. Delivery
///     goes through <see cref="ISmtpEmailTransport" /> (MailKit, pinned connection, SEC-08), so implicit TLS on 465,
///     STARTTLS, plain connections, and a custom EHLO domain all work.
/// </summary>
public sealed class EmailNotificationProvider(
    ISmtpEmailTransport smtpTransport,
    InstanceEmailSender instanceSender) : INotificationProvider
{
    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<NotificationDeliveryResult> SendAsync(
        NotificationEventDispatchMessage notification,
        NotificationTargetDispatchContext target,
        CancellationToken cancellationToken = default)
    {
        EmailNotificationSecret secret;
        try
        {
            secret = NotificationJson.ParseSecret<EmailNotificationSecret>(target.SecretJson);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
        {
            return NotificationDeliveryResult.ConfigurationError("Email notification credentials are not valid.");
        }

        var recipients = (secret.To ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (recipients.Length == 0)
            return NotificationDeliveryResult.ConfigurationError("Email notification target has no recipients.");

        var message = Render(notification, recipients);
        EmailSendResult result;
        if (secret.UseSystemSettings)
        {
            result = await instanceSender.SendAsync(message, EmailPurpose.Notification, cancellationToken);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(secret.SmtpHost) || string.IsNullOrWhiteSpace(secret.From))
                return NotificationDeliveryResult.ConfigurationError("Email notification SMTP settings are incomplete.");

            var port = secret.SmtpPort is > 0 and <= 65535 ? secret.SmtpPort.Value : 587;
            var encryption = ResolveEncryption(secret, port);
            if (encryption is null)
                return NotificationDeliveryResult.ConfigurationError("Email notification encryption mode is not valid.");

            var settings = new SmtpTransportSettings(SmtpServerSettings.NormalizeHost(secret.SmtpHost), port,
                encryption.Value, secret.Username, secret.Password, secret.EhloDomain,
                TimeSpan.FromSeconds(secret.TimeoutSeconds is > 0 and <= 300 ? secret.TimeoutSeconds.Value : 30));
            result = await smtpTransport.SendAsync(settings, new EmailSender(secret.From.Trim(), secret.FromName),
                message, cancellationToken);
        }

        return result.Success
            ? NotificationDeliveryResult.Succeeded(result.ProviderMessageId)
            : NotificationDeliveryResult.Failed($"Email notification delivery failed: {result.FailureReason}",
                result.FailureKind);
    }

    internal static SmtpEncryption? ResolveEncryption(EmailNotificationSecret secret, int port)
    {
        if (!string.IsNullOrWhiteSpace(secret.Encryption))
        {
            try
            {
                return SmtpEncryptionNames.Parse(secret.Encryption);
            }
            catch (Vessel.Domain.Common.DomainException)
            {
                return null;
            }
        }

        // Targets saved before NTF-06 only had EnableSsl (System.Net.Mail semantics: STARTTLS).
        if (!secret.EnableSsl)
            return SmtpEncryption.None;
        return port == 465 ? SmtpEncryption.Tls : SmtpEncryption.StartTls;
    }

    private static EmailMessage Render(NotificationEventDispatchMessage notification, string[] recipients)
    {
        var subject = $"[{notification.Severity}] {notification.Title}";
        var link = notification.ResourceUrl;
        var text = string.IsNullOrWhiteSpace(link)
            ? notification.Message
            : $"{notification.Message}{Environment.NewLine}{Environment.NewLine}{link}";
        var linkHtml = link is not null && Uri.TryCreate(link, UriKind.Absolute, out var absolute) &&
                       (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp)
            ? $"<p><a href=\"{WebUtility.HtmlEncode(absolute.ToString())}\">Open in Vessel</a></p>"
            : string.Empty;
        var html = $"""
                    <!doctype html>
                    <html><body style="font-family:sans-serif;line-height:1.5;color:#1f2933">
                    <h2 style="font-size:16px">{WebUtility.HtmlEncode(notification.Title)}</h2>
                    <p>{WebUtility.HtmlEncode(notification.Message)}</p>
                    {linkHtml}
                    <p style="font-size:12px;color:#52606d">{WebUtility.HtmlEncode(notification.EventType)} · {WebUtility.HtmlEncode(notification.Severity.ToString())}</p>
                    </body></html>
                    """;
        return new EmailMessage(recipients, subject, text, html);
    }
}
