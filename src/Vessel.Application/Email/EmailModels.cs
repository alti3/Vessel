using Vessel.Domain.Notifications;
using Vessel.Domain.Settings;

namespace Vessel.Application.Email;

/// <summary>A rendered email. Bodies may contain one-time links and must never be logged.</summary>
public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string TextBody, string? HtmlBody = null);

public sealed record EmailSender(string Address, string? Name);

/// <summary>Which instance sender is used (v4.7.0 sender split).</summary>
public enum EmailPurpose
{
    /// <summary>Password reset, invitations, verification: the active provider's own sender.</summary>
    Transactional = 0,

    /// <summary>Team notifications and channel-disabled alerts: <c>notification_from_*</c>, falling back to transactional.</summary>
    Notification = 1
}

/// <summary>
///     Result of one send. <see cref="FailureKind" /> follows the notification classification: transient failures
///     (connection errors, 4xx SMTP replies, HTTP 429/5xx) are retried; configuration failures (refused login, blocked
///     host, invalid API key) are not. Reasons never contain server replies, credentials, or message content.
/// </summary>
public sealed record EmailSendResult(
    bool Success,
    string? ProviderMessageId,
    string? FailureReason,
    NotificationFailureKind FailureKind = NotificationFailureKind.Transient)
{
    public static EmailSendResult Sent(string? providerMessageId = null)
    {
        return new EmailSendResult(true, providerMessageId, null);
    }

    public static EmailSendResult Failed(string reason, NotificationFailureKind kind)
    {
        return new EmailSendResult(false, null, reason, kind);
    }
}

/// <summary>Connection settings for one SMTP send. <see cref="Password" /> is plaintext in memory only.</summary>
public sealed record SmtpTransportSettings(
    string Host,
    int Port,
    SmtpEncryption Encryption,
    string? Username,
    string? Password,
    string? EhloDomain,
    TimeSpan Timeout)
{
    public override string ToString()
    {
        // Never print the password (records include every property by default).
        return $"SmtpTransportSettings {{ Host = {Host}, Port = {Port}, Encryption = {Encryption} }}";
    }
}

/// <summary>
///     SMTP delivery. Implementations must resolve the host once, refuse addresses the outbound policy blocks, and
///     connect only to the checked address (no DNS-rebinding window), verifying TLS against the original host name.
/// </summary>
public interface ISmtpEmailTransport
{
    Task<EmailSendResult> SendAsync(SmtpTransportSettings settings, EmailSender from, EmailMessage message,
        CancellationToken cancellationToken = default);
}

/// <summary>Resend HTTP API delivery (fixed endpoint <c>api.resend.com</c>).</summary>
public interface IResendEmailTransport
{
    Task<EmailSendResult> SendAsync(string apiKey, EmailSender from, EmailMessage message,
        CancellationToken cancellationToken = default);
}

/// <summary>SMTP settings of common providers (upstream <c>SmtpPresets</c>). A preset fills only connection fields.</summary>
public sealed record SmtpPreset(string Key, string Label, string Host, int Port, SmtpEncryption Encryption,
    string? Username = null);

public static class SmtpPresets
{
    public static readonly IReadOnlyList<SmtpPreset> All =
    [
        new("postshiba", "PostShiba", "smtp.postshiba.com", 587, SmtpEncryption.StartTls),
        new("amazon-ses", "Amazon SES (us-east-1)", "email-smtp.us-east-1.amazonaws.com", 587, SmtpEncryption.StartTls),
        new("brevo", "Brevo", "smtp-relay.brevo.com", 587, SmtpEncryption.StartTls),
        new("gmail", "Gmail", "smtp.gmail.com", 587, SmtpEncryption.StartTls),
        new("mailgun", "Mailgun", "smtp.mailgun.org", 587, SmtpEncryption.StartTls),
        new("mailjet", "Mailjet", "in-v3.mailjet.com", 587, SmtpEncryption.StartTls),
        new("microsoft-365", "Microsoft 365", "smtp.office365.com", 587, SmtpEncryption.StartTls),
        new("postmark", "Postmark", "smtp.postmarkapp.com", 587, SmtpEncryption.StartTls),
        new("proton-mail", "Proton Mail", "smtp.protonmail.ch", 587, SmtpEncryption.StartTls),
        new("resend", "Resend", "smtp.resend.com", 465, SmtpEncryption.Tls, "resend"),
        new("sendgrid", "SendGrid", "smtp.sendgrid.net", 587, SmtpEncryption.StartTls, "apikey"),
        new("zoho", "Zoho Mail", "smtp.zoho.com", 465, SmtpEncryption.Tls)
    ];

    /// <summary>Whether the host belongs to a preset provider; all of them need a username and password.</summary>
    public static bool RequiresLogin(string? host)
    {
        var normalized = (host ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Length > 0 && All.Any(preset => preset.Host == normalized);
    }
}

public static class SmtpEncryptionNames
{
    public static string ToName(SmtpEncryption encryption)
    {
        return encryption switch
        {
            SmtpEncryption.None => "none",
            SmtpEncryption.Tls => "tls",
            _ => "starttls"
        };
    }

    /// <summary>Parses upstream names (<c>starttls</c>, <c>tls</c>, <c>ssl</c>, <c>none</c>).</summary>
    public static SmtpEncryption Parse(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "starttls" => SmtpEncryption.StartTls,
            "tls" or "ssl" or "smtps" => SmtpEncryption.Tls,
            "none" => SmtpEncryption.None,
            _ => throw new Vessel.Domain.Common.DomainException(
                "The SMTP encryption must be one of starttls, tls, or none.")
        };
    }
}
