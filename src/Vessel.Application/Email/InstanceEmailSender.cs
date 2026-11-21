using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain.Notifications;
using Vessel.Domain.Settings;

namespace Vessel.Application.Email;

/// <summary>
///     Sends mail through the instance email settings (NTF-06): the active SMTP server or Resend. Used for
///     transactional mail (IAM-10), channel-disabled alerts, and notification targets that use the system settings.
/// </summary>
public sealed class InstanceEmailSender(
    IVesselDbContext dbContext,
    ISecretCipher cipher,
    ISmtpEmailTransport smtpTransport,
    IResendEmailTransport resendTransport)
{
    public const string SmtpPasswordPurpose = "vessel:instance-email:smtp-password";
    public const string ResendApiKeyPurpose = "vessel:instance-email:resend-api-key";

    /// <summary>True when an instance provider is configured and complete.</summary>
    public bool IsConfigured()
    {
        return Load()?.IsEnabled == true;
    }

    public Task<EmailSendResult> SendAsync(EmailMessage message, EmailPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        var settings = Load();
        return settings is null || !settings.IsEnabled
            ? Task.FromResult(EmailSendResult.Failed("Instance email is not configured.",
                NotificationFailureKind.Configuration))
            : SendAsync(settings, message, purpose, cancellationToken);
    }

    internal async Task<EmailSendResult> SendAsync(InstanceEmailSettings settings, EmailMessage message,
        EmailPurpose purpose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.To.Count == 0)
            return EmailSendResult.Failed("The email has no recipients.", NotificationFailureKind.Payload);

        try
        {
            switch (settings.Provider)
            {
                case EmailProvider.Smtp when settings.ActiveSmtpServer is { } server:
                {
                    var password = server.ProtectedPassword is null
                        ? null
                        : cipher.Unprotect(server.ProtectedPassword, SmtpPasswordPurpose);
                    var transport = new SmtpTransportSettings(server.Host, server.Port, server.Encryption,
                        server.Username, password, server.EhloDomain, TimeSpan.FromSeconds(server.TimeoutSeconds));
                    var from = SenderFor(settings, purpose, new EmailSender(server.FromAddress, server.FromName));
                    return await smtpTransport.SendAsync(transport, from, message, cancellationToken);
                }
                case EmailProvider.Resend when settings.ProtectedResendApiKey is not null
                                               && settings.ResendFromAddress is not null:
                {
                    var apiKey = cipher.Unprotect(settings.ProtectedResendApiKey, ResendApiKeyPurpose);
                    var from = SenderFor(settings, purpose,
                        new EmailSender(settings.ResendFromAddress, settings.ResendFromName));
                    return await resendTransport.SendAsync(apiKey, from, message, cancellationToken);
                }
                default:
                    return EmailSendResult.Failed("Instance email is not configured.",
                        NotificationFailureKind.Configuration);
            }
        }
        catch (SecretDecryptionException)
        {
            return EmailSendResult.Failed("Instance email credentials could not be decrypted with the key ring.",
                NotificationFailureKind.Configuration);
        }
    }

    private InstanceEmailSettings? Load()
    {
        return dbContext.InstanceEmailSettings.FirstOrDefault();
    }

    private static EmailSender SenderFor(InstanceEmailSettings settings, EmailPurpose purpose, EmailSender transactional)
    {
        return purpose == EmailPurpose.Notification && settings.NotificationFromAddress is not null
            ? new EmailSender(settings.NotificationFromAddress, settings.NotificationFromName ?? transactional.Name)
            : transactional;
    }
}
