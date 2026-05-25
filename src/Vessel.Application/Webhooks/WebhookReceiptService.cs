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
using AppEntity = Vessel.Domain.Applications.Application;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.Application.Webhooks;

public sealed class WebhookReceiptService(
    IVesselDbContext dbContext,
    IBackgroundJobDispatcher backgroundJobs,
    ISecretVault secretVault,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
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
        var verified = await VerifyAnyMatchAsync(request.Provider, parsed, request.Headers, rawBody,
            request.PayloadJson, cancellationToken);
        if (!verified)
        {
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

        webhookEvent.MarkSignatureVerified(now);
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

    private async Task<bool> VerifyAnyMatchAsync(
        WebhookProvider provider,
        ParsedWebhook? parsed,
        IReadOnlyDictionary<string, string> headers,
        string rawBody,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        if (parsed is null) return false;

        IEnumerable<AppEntity> candidates = parsed.ApplicationId.HasValue
            ? dbContext.Applications.AsEnumerable()
                .Where(application => application.Id.Value == parsed.ApplicationId.Value)
            : dbContext.Applications.AsEnumerable()
                .Where(application => parsed.Branch is not null && application.GitSource.Branch == parsed.Branch)
                .Where(application => parsed.Repository is not null &&
                                      application.GitSource.RepositoryUrl.Value.Contains(parsed.Repository,
                                          StringComparison.OrdinalIgnoreCase));

        foreach (AppEntity application in candidates)
        {
            ApplicationWebhookConfiguration? configuration =
                dbContext.ApplicationWebhookConfigurations.SingleOrDefault(configuration =>
                    configuration.ApplicationId == application.Id &&
                    configuration.Provider == provider &&
                    configuration.IsEnabled);
            if (configuration is null) continue;

            var secret = await secretVault.RevealForDeploymentAsync(TeamForApplication(application),
                configuration.SecretReferenceId, cancellationToken);
            var supplied = provider switch
            {
                WebhookProvider.GitHub => WebhookPayloadReader.Header(headers, "X-Hub-Signature-256"),
                WebhookProvider.Gitea => WebhookPayloadReader.Header(headers, "X-Hub-Signature-256"),
                WebhookProvider.GitLab => WebhookPayloadReader.Header(headers, "X-Gitlab-Token"),
                WebhookProvider.Bitbucket => WebhookPayloadReader.Header(headers, "X-Hub-Signature"),
                WebhookProvider.Generic => GenericSecret(payloadJson),
                _ => null
            };

            var verified = provider switch
            {
                WebhookProvider.GitHub or WebhookProvider.Gitea or WebhookProvider.Bitbucket => VerifySha256Signature(
                    supplied, rawBody, secret),
                WebhookProvider.GitLab or WebhookProvider.Generic => FixedEquals(secret, supplied),
                _ => false
            };
            if (verified) return true;
        }

        return false;
    }

    private TeamId TeamForApplication(AppEntity application)
    {
        EnvironmentEntity environment =
            dbContext.Environments.Single(environment => environment.Id == application.EnvironmentId);
        return dbContext.Projects.Single(project => project.Id == environment.ProjectId).TeamId;
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

    private static string? GenericSecret(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return WebhookPayloadReader.Text(document.RootElement, "secret");
    }

    private static bool VerifySha256Signature(string? header, string payloadJson, string secret)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        var signature = header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? header["sha256=".Length..]
            : header;
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payloadJson));
        return FixedEquals(Convert.ToHexString(hash).ToLowerInvariant(), signature);
    }

    private static bool FixedEquals(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
