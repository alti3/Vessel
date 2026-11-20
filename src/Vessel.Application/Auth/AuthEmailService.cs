using System.Globalization;
using System.Net;
using Vessel.Application.Auditing;
using Vessel.Application.Common;
using Vessel.Application.Email;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Users;

namespace Vessel.Application.Auth;

/// <summary>
///     Transactional auth email (IAM-10): password reset, team invitation, and email verification. Messages go
///     through the instance email settings (NTF-06) as background jobs with retries. Links are built only from the
///     configured instance URL (<c>Vessel:Host:PublicUrl</c>), never from the request <c>Host</c> header; when either
///     the URL or the instance email settings are missing, nothing is queued. Tokens are never logged or audited.
/// </summary>
public sealed class AuthEmailService(
    IVesselDbContext dbContext,
    InstanceEmailOutbox emailOutbox,
    InstanceEmailSender emailSender,
    InstanceLinkBuilder links,
    ISecretCipher cipher,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan VerificationTokenLifetime = TimeSpan.FromHours(24);
    private const string VerificationPurpose = "vessel:auth:email-verification";

    /// <summary>True when a public URL and a complete instance email provider are configured.</summary>
    public bool CanSend => links.IsConfigured && emailSender.IsConfigured();

    public bool QueuePasswordReset(User user, string token, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(user);
        var link = links.Build("/password-reset", new Dictionary<string, string>
        {
            ["email"] = user.Email.Value,
            ["token"] = token
        });
        if (link is null || !emailSender.IsConfigured())
            return false;

        var minutes = Math.Max(1, (int)Math.Round((expiresAt - timeProvider.GetUtcNow()).TotalMinutes));
        return TryEnqueue(Render(user.Email.Value, "Vessel: reset your password",
            $"Hello {user.Name.Value},",
            "Someone (hopefully you) asked to reset the password of your Vessel account. Use the link below to " +
            $"choose a new password. The link expires in {minutes.ToString(CultureInfo.InvariantCulture)} minutes.",
            link, "Reset password",
            "If you did not ask for this, ignore this email; your password stays the same."));
    }

    public bool QueueInvitation(string email, string teamName, string invitationToken, DateTimeOffset expiresAt)
    {
        var link = links.Build("/invitations/accept", new Dictionary<string, string> { ["token"] = invitationToken });
        if (link is null || !emailSender.IsConfigured())
            return false;

        return TryEnqueue(Render(email, $"Vessel: you are invited to join {teamName}",
            "Hello,",
            $"You were invited to join the team '{teamName}' on Vessel. Open the link below, sign in or create an " +
            $"account with this email address, and confirm the invitation. The invitation expires on " +
            $"{expiresAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}.",
            link, "Review invitation",
            "If you do not expect this invitation, ignore this email."));
    }

    public bool QueueEmailVerification(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.EmailVerifiedAt.HasValue)
            return false;

        var link = links.Build("/verify-email", new Dictionary<string, string>
        {
            ["token"] = CreateVerificationToken(user, timeProvider.GetUtcNow().Add(VerificationTokenLifetime))
        });
        if (link is null || !emailSender.IsConfigured())
            return false;

        return TryEnqueue(Render(user.Email.Value, "Vessel: verify your email address",
            $"Hello {user.Name.Value},",
            "Please confirm that this email address belongs to your Vessel account. The link expires in 24 hours.",
            link, "Verify email address",
            "If you did not create a Vessel account, ignore this email."));
    }

    /// <summary>Sends a new verification email to the signed-in user.</summary>
    public async Task<bool> RequestEmailVerificationAsync(UserId userId, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.UserRepository.GetByIdAsync(userId, cancellationToken)
                   ?? throw new InvalidOperationException("User was not found.");
        return QueueEmailVerification(user);
    }

    /// <summary>
    ///     Confirms an email verification token. The token is bound to the user id and the email address it was sent
    ///     to, so it stops working when the address changes. Returns <c>false</c> for invalid or expired tokens.
    /// </summary>
    public async Task<bool> ConfirmEmailVerificationAsync(string token, string? correlationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048)
            return false;

        string payload;
        try
        {
            payload = cipher.Unprotect(token.Trim(), VerificationPurpose);
        }
        catch (Exception exception) when (exception is SecretDecryptionException or FormatException)
        {
            return false;
        }

        var parts = payload.Split('|');
        if (parts.Length != 3
            || !Guid.TryParseExact(parts[0], "N", out var userGuid)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresUnix)
            || DateTimeOffset.FromUnixTimeSeconds(expiresUnix) <= timeProvider.GetUtcNow())
            return false;

        var user = await dbContext.UserRepository.GetByIdAsync(new UserId(userGuid), cancellationToken);
        if (user is null || !string.Equals(user.Email.Value, parts[1], StringComparison.Ordinal))
            return false;
        if (user.EmailVerifiedAt.HasValue)
            return true;

        user.VerifyEmail(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(null, user.Id, AuditActions.EmailVerified,
            new AuditTarget("user", user.Id.ToString()), correlationId, new Dictionary<string, object?>(),
            cancellationToken);
        return true;
    }

    public string CreateVerificationToken(User user, DateTimeOffset expiresAt)
    {
        return cipher.Protect(
            $"{user.Id.Value:N}|{user.Email.Value}|{expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}",
            VerificationPurpose);
    }

    private bool TryEnqueue(EmailMessage message)
    {
        try
        {
            emailOutbox.Enqueue(message, EmailPurpose.Transactional);
            return true;
        }
#pragma warning disable CA1031 // Queueing must never fail the auth flow or reveal whether an account exists.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private static EmailMessage Render(string to, string subject, string greeting, string body, Uri link,
        string action, string footer)
    {
        var url = link.AbsoluteUri;
        var text = $"{greeting}\n\n{body}\n\n{url}\n\n{footer}\n";
        var html = $"""
                    <!doctype html>
                    <html><body style="font-family:sans-serif;line-height:1.5;color:#1f2933">
                    <p>{WebUtility.HtmlEncode(greeting)}</p>
                    <p>{WebUtility.HtmlEncode(body)}</p>
                    <p><a href="{WebUtility.HtmlEncode(url)}" style="display:inline-block;padding:10px 16px;background:#2563eb;color:#fff;text-decoration:none;border-radius:6px">{WebUtility.HtmlEncode(action)}</a></p>
                    <p style="font-size:12px;color:#52606d">{WebUtility.HtmlEncode(url)}</p>
                    <p style="font-size:12px;color:#52606d">{WebUtility.HtmlEncode(footer)}</p>
                    </body></html>
                    """;
        return new EmailMessage([to], subject, text, html);
    }
}
