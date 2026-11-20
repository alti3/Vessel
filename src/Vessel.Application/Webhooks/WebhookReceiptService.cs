using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel.Application.Auditing;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Webhooks;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Webhooks;

public sealed class WebhookReceiptService(
    IVesselDbContext dbContext,
    IBackgroundJobDispatcher backgroundJobs,
    ISecretVault secretVault,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    WebhookFailureThrottle? failureThrottle = null)
{
    private const int MaxPayloadBytes = 256 * 1024;

    public async Task<WebhookReceiptResult> ReceiveAsync(
        WebhookReceiptRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(request.PayloadJson) > MaxPayloadBytes)
            throw new InvalidOperationException("Webhook payload exceeds the configured 256 KiB limit.");

        using var document = JsonDocument.Parse(request.PayloadJson);
        JsonElement payload = WebhookPayloadReader.PayloadRoot(document.RootElement);
        var eventType = WebhookPayloadReader.EventType(request.Provider, request.Headers, payload);
        var providerEventId = WebhookPayloadReader.ProviderEventId(request.Provider, request.Headers, payload);
        var rawBody = WebhookPayloadReader.RawBody(document.RootElement);
        if (string.Equals(rawBody, payload.GetRawText(), StringComparison.Ordinal))
            rawBody = request.PayloadJson;
        var dedupeKey = $"{request.Provider}:{eventType}:{providerEventId ?? Sha256(rawBody)}";
        DateTimeOffset now = timeProvider.GetUtcNow();
        var sanitizedPayload = SanitizePayload(request.Provider, request.PayloadJson);
        var sanitizedEnvelope = JsonSerializer.Serialize(new
        {
            headers = SanitizeHeaders(request.Provider, request.Headers),
            rawBody = sanitizedPayload,
            payload = JsonSerializer.Deserialize<JsonElement>(sanitizedPayload)
        });

        var webhookEvent = WebhookEvent.Receive(
            request.Provider,
            eventType,
            providerEventId,
            dedupeKey,
            sanitizedEnvelope,
            WebhookSignatureStatus.Missing,
            now);

        if (dbContext.WebhookEvents.Any(existing => existing.DedupeKey == dedupeKey))
        {
            webhookEvent.MarkDuplicate(now);
            await dbContext.WebhookEventRepository.AddAsync(webhookEvent, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new WebhookReceiptResult(webhookEvent.Id.Value, request.Provider, eventType, webhookEvent.Status,
                webhookEvent.SignatureStatus, "Duplicate webhook ignored.");
        }

        ParsedWebhook? parsed = WebhookPayloadReader.Parse(request.Provider, request.Headers, payload);
        // SEC-01: each candidate application is verified against its own secret only; the event records exactly which
        // applications verified, and processing acts on those applications alone.
        IReadOnlyList<AppId> verifiedApplications = parsed is null
            ? []
            : await WebhookApplicationMatching.VerifyAsync(dbContext, secretVault, request.Provider,
                WebhookApplicationMatching.Candidates(dbContext, parsed), request.Headers, rawBody,
                request.PayloadJson, cancellationToken);
        if (verifiedApplications.Count == 0)
        {
            // SEC-07: count distinct failed attempts per provider + client address + repository + branch. Correctly
            // signed deliveries never reach this branch, so they are processed even while the scope is locked.
            if (failureThrottle is not null)
            {
                var scopeKey = WebhookFailureThrottle.ScopeKey(request.Provider, request.ClientAddress,
                    parsed?.Repository, parsed?.Branch);
                if (await failureThrottle.IsLockedAsync(scopeKey, cancellationToken))
                    return new WebhookReceiptResult(Guid.Empty, request.Provider, eventType,
                        WebhookEventStatus.Rejected, WebhookSignatureStatus.Failed,
                        "Too many failed webhook authentication attempts. Try again later.",
                        (int)failureThrottle.RetryAfter().TotalSeconds);

                await failureThrottle.RecordFailureAsync(scopeKey,
                    WebhookFailureThrottle.AttemptIdentity(request.Provider,
                        WebhookApplicationMatching.SuppliedCredential(request.Provider, request.Headers,
                            request.PayloadJson), rawBody),
                    cancellationToken);
            }

            webhookEvent.Reject("Webhook signature or token verification failed.", now);
            await dbContext.WebhookEventRepository.AddAsync(webhookEvent, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await auditWriter.RecordAsync(null, null, AuditActions.WebhookRejected,
                new AuditTarget("webhook-event", webhookEvent.Id.Value.ToString("D")), null,
                new Dictionary<string, object?>
                { ["provider"] = request.Provider.ToString(), ["eventType"] = eventType },
                cancellationToken);
            return new WebhookReceiptResult(webhookEvent.Id.Value, request.Provider, eventType, webhookEvent.Status,
                webhookEvent.SignatureStatus, "Webhook signature or token verification failed.");
        }

        webhookEvent.MarkSignatureVerified(verifiedApplications, now);
        await dbContext.WebhookEventRepository.AddAsync(webhookEvent, cancellationToken);
        webhookEvent.MarkQueued(now);
        await dbContext.SaveChangesAsync(cancellationToken);

        backgroundJobs.Enqueue<ProcessWebhookEventJob>(job =>
            job.ProcessAsync(webhookEvent.Id.Value, CancellationToken.None));
        await auditWriter.RecordAsync(null, null, AuditActions.WebhookReceived,
            new AuditTarget("webhook-event", webhookEvent.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["provider"] = request.Provider.ToString(), ["eventType"] = eventType },
            cancellationToken);

        return new WebhookReceiptResult(webhookEvent.Id.Value, request.Provider, eventType, webhookEvent.Status,
            webhookEvent.SignatureStatus, "Webhook accepted for processing.");
    }

    private static string Sha256(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, string> SanitizeHeaders(WebhookProvider provider,
        IReadOnlyDictionary<string, string> headers)
    {
        return headers.ToDictionary(pair => pair.Key,
            pair => IsSecretHeader(provider, pair.Key) ? "[redacted]" : pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSecretHeader(WebhookProvider provider, string name)
    {
        return provider == WebhookProvider.GitLab &&
               string.Equals(name, "X-Gitlab-Token", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizePayload(WebhookProvider provider, string payloadJson)
    {
        if (provider != WebhookProvider.Generic) return payloadJson;
        using var document = JsonDocument.Parse(payloadJson);
        var values = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name,
                property => property.NameEquals("secret") ? (object?)"[redacted]" : property.Value.Clone(),
                StringComparer.Ordinal);
        return JsonSerializer.Serialize(values);
    }
}
