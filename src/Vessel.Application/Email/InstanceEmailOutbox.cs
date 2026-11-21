using System.Text.Json;
using Vessel.Application.Auditing;
using Vessel.Application.Jobs;
using Vessel.Application.Security;
using Vessel.Domain.Auditing;
using Vessel.Domain.Notifications;

namespace Vessel.Application.Email;

/// <summary>
///     Queues instance email (password reset, invitations, verification, channel-disabled alerts) for background
///     delivery with Hangfire retries. Hangfire persists job arguments, and these messages carry one-time tokens, so
///     the rendered message is encrypted with the secret key ring before it is enqueued.
/// </summary>
public sealed class InstanceEmailOutbox(IBackgroundJobDispatcher backgroundJobs, ISecretCipher cipher)
{
    internal const string MessagePurpose = "vessel:instance-email:queued-message";

    public string Enqueue(EmailMessage message, EmailPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(message);
        var protectedMessage = cipher.Protect(JsonSerializer.Serialize(message), MessagePurpose);
        return backgroundJobs.Enqueue<SendInstanceEmailJob>(job =>
            job.RunAsync(protectedMessage, purpose, CancellationToken.None));
    }

    internal EmailMessage Read(string protectedMessage)
    {
        return JsonSerializer.Deserialize<EmailMessage>(cipher.Unprotect(protectedMessage, MessagePurpose))
               ?? throw new InvalidOperationException("Queued email could not be read.");
    }
}

/// <summary>
///     Hangfire entry point for <see cref="InstanceEmailOutbox" />. Transient failures throw so Hangfire retries;
///     permanent failures (configuration or rejected message) are audited and not retried.
/// </summary>
public sealed class SendInstanceEmailJob(
    InstanceEmailOutbox outbox,
    InstanceEmailSender sender,
    IAuditWriter auditWriter)
{
    public async Task RunAsync(string protectedMessage, EmailPurpose purpose, CancellationToken cancellationToken)
    {
        EmailMessage message;
        try
        {
            message = outbox.Read(protectedMessage);
        }
        catch (Exception exception) when (exception is SecretDecryptionException or JsonException
                                              or InvalidOperationException)
        {
            await AuditFailureAsync(purpose, "The queued email could not be decrypted.", cancellationToken);
            return;
        }

        var result = await sender.SendAsync(message, purpose, cancellationToken);
        if (result.Success)
            return;
        if (result.FailureKind == NotificationFailureKind.Transient)
            throw new EmailDeliveryException(result.FailureReason ?? "Email delivery failed.");

        await AuditFailureAsync(purpose, result.FailureReason ?? "Email delivery failed.", cancellationToken);
    }

    private Task AuditFailureAsync(EmailPurpose purpose, string reason, CancellationToken cancellationToken)
    {
        return auditWriter.RecordAsync(null, null, "email.delivery_failed",
            new AuditTarget("instance_email", purpose.ToString()), null,
            new Dictionary<string, object?> { ["reason"] = reason, ["purpose"] = purpose.ToString() },
            cancellationToken);
    }
}

/// <summary>A transient email failure; thrown so the background job is retried. The message is safe to log.</summary>
public sealed class EmailDeliveryException(string message) : Exception(message);
