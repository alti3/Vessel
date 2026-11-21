using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Vessel.Application.Email;
using Vessel.Domain.Notifications;
using Vessel.Domain.Settings;
using Vessel.Infrastructure.OutboundRequests;

namespace Vessel.Infrastructure.Email;

/// <summary>
///     SMTP delivery through MailKit (NTF-06 / SEC-08). <c>System.Net.Mail.SmtpClient</c> cannot do implicit TLS on
///     port 465, set the EHLO domain, or connect to a pre-checked address; MailKit supports all three:
///     <list type="bullet">
///         <item><see cref="SmtpEncryption.None" /> → <see cref="SecureSocketOptions.None" /> (no opportunistic STARTTLS).</item>
///         <item><see cref="SmtpEncryption.StartTls" /> → <see cref="SecureSocketOptions.StartTls" /> (required).</item>
///         <item><see cref="SmtpEncryption.Tls" /> → <see cref="SecureSocketOptions.SslOnConnect" /> (implicit TLS).</item>
///     </list>
///     The socket comes from <see cref="PinnedSocketConnector" /> (resolved once, policy-checked, connected only to the
///     checked address); MailKit receives the original host name for SNI and certificate validation.
/// </summary>
public sealed class MailKitSmtpTransport(PinnedSocketConnector connector) : ISmtpEmailTransport
{
    public async Task<EmailSendResult> SendAsync(SmtpTransportSettings settings, EmailSender from,
        EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(message);

        MimeMessage mime;
        try
        {
            mime = BuildMessage(from, message);
        }
        catch (Exception exception) when (exception is ParseException or ArgumentException or FormatException)
        {
            return EmailSendResult.Failed("The email sender or recipient address is not valid.",
                NotificationFailureKind.Configuration);
        }

        Socket socket;
        try
        {
            socket = await connector.ConnectAsync(settings.Host, settings.Port, settings.Timeout, cancellationToken);
        }
        catch (OutboundRequestBlockedException exception)
        {
            return EmailSendResult.Failed($"The SMTP host is not allowed: {SafeReason(exception)}",
                NotificationFailureKind.Configuration);
        }
        catch (OutboundHostUnresolvedException)
        {
            return EmailSendResult.Failed("The SMTP host could not be resolved.", NotificationFailureKind.Transient);
        }
        catch (IOException)
        {
            return EmailSendResult.Failed("Could not connect to the SMTP server.", NotificationFailureKind.Transient);
        }

        using (socket)
        using (var client = new SmtpClient())
        {
            client.Timeout = (int)Math.Clamp(settings.Timeout.TotalMilliseconds, 1_000, 300_000);
            if (!string.IsNullOrWhiteSpace(settings.EhloDomain))
                client.LocalDomain = settings.EhloDomain;

            try
            {
                await client.ConnectAsync(socket, settings.Host, settings.Port, ToSecureSocketOptions(settings.Encryption),
                    cancellationToken);
                if (!string.IsNullOrEmpty(settings.Username))
                    await client.AuthenticateAsync(settings.Username, settings.Password ?? string.Empty,
                        cancellationToken);

                await client.SendAsync(mime, cancellationToken);
                await client.DisconnectAsync(true, cancellationToken);
                return EmailSendResult.Sent(mime.MessageId);
            }
            catch (AuthenticationException)
            {
                return EmailSendResult.Failed("The SMTP server refused the login.", NotificationFailureKind.Configuration);
            }
            catch (SslHandshakeException)
            {
                return EmailSendResult.Failed("The TLS handshake with the SMTP server failed.",
                    NotificationFailureKind.Configuration);
            }
            catch (NotSupportedException)
            {
                return EmailSendResult.Failed(
                    "The SMTP server does not support the configured encryption or authentication.",
                    NotificationFailureKind.Configuration);
            }
            catch (SmtpCommandException exception)
            {
                return Classify(exception);
            }
            catch (Exception exception) when (exception is SmtpProtocolException or IOException or SocketException
                                                  or ServiceNotConnectedException or TimeoutException
                                                  || (exception is OperationCanceledException &&
                                                      !cancellationToken.IsCancellationRequested))
            {
                return EmailSendResult.Failed($"SMTP delivery failed: {exception.GetType().Name}.",
                    NotificationFailureKind.Transient);
            }
        }
    }

    public static SecureSocketOptions ToSecureSocketOptions(SmtpEncryption encryption)
    {
        return encryption switch
        {
            SmtpEncryption.None => SecureSocketOptions.None,
            SmtpEncryption.Tls => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.StartTls
        };
    }

    public static MimeMessage BuildMessage(EmailSender from, EmailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(from.Name ?? string.Empty, from.Address));
        foreach (var recipient in message.To)
            mime.To.Add(MailboxAddress.Parse(recipient));
        mime.Subject = message.Subject.ReplaceLineEndings(" ");

        var body = new BodyBuilder { TextBody = message.TextBody };
        if (!string.IsNullOrEmpty(message.HtmlBody))
            body.HtmlBody = message.HtmlBody;
        mime.Body = body.ToMessageBody();
        return mime;
    }

    /// <summary>
    ///     4xx replies are temporary; 530/534/535/538 mean the login or policy is wrong; a refused sender is a
    ///     configuration error; other 5xx (for example an unknown recipient) reject only this message. The server's
    ///     reply text is never included because it can echo addresses or credentials.
    /// </summary>
    private static EmailSendResult Classify(SmtpCommandException exception)
    {
        var code = (int)exception.StatusCode;
        var kind = code switch
        {
            >= 400 and < 500 => NotificationFailureKind.Transient,
            530 or 534 or 535 or 538 => NotificationFailureKind.Configuration,
            _ when exception.ErrorCode == SmtpErrorCode.SenderNotAccepted => NotificationFailureKind.Configuration,
            _ => NotificationFailureKind.Payload
        };
        return EmailSendResult.Failed($"The SMTP server rejected the message (reply {code}).", kind);
    }

    private static string SafeReason(OutboundRequestBlockedException exception)
    {
        const string prefix = "Outbound request blocked: ";
        return exception.Message.StartsWith(prefix, StringComparison.Ordinal)
            ? exception.Message[prefix.Length..]
            : "the address is not allowed.";
    }
}
