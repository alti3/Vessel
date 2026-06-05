using System.Net;
using System.Net.Mail;
using Vessel.Application.Notifications;
using Vessel.Domain.Notifications;

namespace Vessel.Infrastructure.Notifications;

public sealed class EmailNotificationProvider : INotificationProvider
{
    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<NotificationDeliveryResult> SendAsync(
        NotificationEventDispatchMessage notification,
        NotificationTargetDispatchContext target,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var secret = NotificationJson.ParseSecret<EmailNotificationSecret>(target.SecretJson);
            using var message = new MailMessage(secret.From, secret.To)
            {
                Subject = $"[{notification.Severity}] {notification.Title}",
                Body = $"{notification.Message}{Environment.NewLine}{Environment.NewLine}{notification.ResourceUrl}",
                IsBodyHtml = false
            };

            using var client = new SmtpClient(secret.SmtpHost, secret.SmtpPort)
            {
                EnableSsl = secret.EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            if (!string.IsNullOrWhiteSpace(secret.Username))
                client.Credentials = new NetworkCredential(secret.Username, secret.Password);

            using var registration = cancellationToken.Register(client.SendAsyncCancel);
            await client.SendMailAsync(message, cancellationToken);
            return NotificationDeliveryResult.Succeeded();
        }
        catch (Exception ex) when (ex is InvalidOperationException or SmtpException or FormatException)
        {
            return NotificationDeliveryResult.Failed(SafeFailure(ex));
        }
    }

    private static string SafeFailure(Exception exception)
    {
        return $"Email notification delivery failed: {exception.GetType().Name}.";
    }
}
