using System.Text.Json;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.OutboundRequests;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Notifications;
using Vessel.Domain.Secrets;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Notifications;

/// <summary>
///     Team notification target management. Reading targets requires <see cref="VesselPermissions.NotificationsRead" />;
///     creating, changing, and test-sending requires <see cref="VesselPermissions.NotificationsWrite" /> (owners and
///     admins only, matching upstream Coolify's <c>NotificationPolicy</c>). Channel secrets are write-only: they are
///     stored in the encrypted secret vault and never returned.
/// </summary>
public sealed class NotificationTargetService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    ISecretVault secretVault,
    IOutboundUrlPolicy outboundUrlPolicy,
    NotificationEventService notificationEvents,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public IReadOnlyList<NotificationTargetListItem> ListTargets(TeamId teamId, UserId actorUserId)
    {
        Require(actorUserId, teamId, VesselPermissions.NotificationsRead);
        return dbContext.NotificationTargets
            .Where(target => target.TeamId == teamId)
            .OrderBy(target => target.Channel)
            .ThenBy(target => target.Name)
            .ToArray()
            .Select(ToListItem)
            .ToArray();
    }

    public bool CanManage(TeamId teamId, UserId actorUserId)
    {
        return authorization.HasPermission(actorUserId, teamId, VesselPermissions.NotificationsWrite);
    }

    public async Task<NotificationTargetListItem> UpsertTargetAsync(
        TeamId teamId,
        UserId actorUserId,
        UpsertNotificationTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.NotificationsWrite);

        var now = timeProvider.GetUtcNow();
        var name = new ResourceName(request.Name);
        var policy = new NotificationDeliveryPolicy(request.MinimumSeverity, request.DeploymentEvents,
            request.BackupEvents, request.ServerEvents);
        var configurationJson = NormalizeJson(request.ConfigurationJson);

        var target = dbContext.NotificationTargets.FirstOrDefault(existing =>
            existing.TeamId == teamId && existing.Channel == request.Channel && existing.Name == name);

        SecretReferenceId? credentialsReferenceId = target?.CredentialsReferenceId;
        var credentialsChanged = false;
        if (!string.IsNullOrWhiteSpace(request.SecretJson))
        {
            var secretJson = NormalizeJson(request.SecretJson);
            await ValidateSecretAsync(request.Channel, secretJson, cancellationToken);
            credentialsChanged = true;
            if (credentialsReferenceId.HasValue)
            {
                await secretVault.ReplaceAsync(credentialsReferenceId.Value, secretJson, cancellationToken);
            }
            else
            {
                var secret = await secretVault.StoreAsync(teamId, SecretScope.Team,
                    $"notification:{request.Channel}:{name.Value}", secretJson,
                    new SecretPolicy(false, false, true), new SecretTarget(), cancellationToken);
                credentialsReferenceId = secret.Id;
            }
        }
        else if (RequiresCredentials(request.Channel) && !credentialsReferenceId.HasValue)
        {
            throw new DomainException($"{request.Channel} notification targets require credentials.");
        }

        if (target is null)
        {
            target = NotificationTarget.Create(teamId, name, request.Channel, credentialsReferenceId, now);
            target.Configure(name, credentialsReferenceId, configurationJson, policy, now);
            if (!request.IsEnabled)
                target.Disable(now);
            else
                target.Enable(now);
            await dbContext.NotificationTargetRepository.AddAsync(target, cancellationToken);
        }
        else
        {
            target.Configure(name, credentialsReferenceId, configurationJson, policy, now);
            if (!request.IsEnabled)
                target.Disable(now);
            else
                target.Enable(now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.NotificationTargetConfigured,
            new Vessel.Domain.Auditing.AuditTarget("notification_target", target.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["channel"] = target.Channel.ToString(),
                ["enabled"] = target.IsEnabled,
                ["credentialsChanged"] = credentialsChanged
            }, cancellationToken);

        return ToListItem(target);
    }

    /// <summary>Queues a fixed-content test notification to one target. Requires notifications.write.</summary>
    public async Task<Guid> SendTestAsync(
        TeamId teamId,
        UserId actorUserId,
        NotificationTargetId targetId,
        CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.NotificationsWrite);
        var target = dbContext.NotificationTargets.FirstOrDefault(item => item.Id == targetId && item.TeamId == teamId)
                     ?? throw new InvalidOperationException("Notification target was not found.");
        if (target.Channel == NotificationChannel.InApp)
            throw new DomainException("In-app targets cannot be test-sent.");

        var eventId = await notificationEvents.RaiseTestAsync(teamId, target.Id, target.Name.Value,
            cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.NotificationTargetTested,
            new Vessel.Domain.Auditing.AuditTarget("notification_target", target.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["channel"] = target.Channel.ToString(),
                ["eventId"] = eventId.ToString("D")
            }, cancellationToken);
        return eventId;
    }

    private void Require(UserId actorUserId, TeamId teamId, string permission)
    {
        if (!authorization.HasPermission(actorUserId, teamId, permission))
            throw new UnauthorizedAccessException($"Missing required permission '{permission}'.");
    }

    private static bool RequiresCredentials(NotificationChannel channel)
    {
        return channel is not NotificationChannel.InApp;
    }

    private async Task ValidateSecretAsync(NotificationChannel channel, string secretJson,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(secretJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new DomainException("Notification credentials must be a JSON object.");

        switch (channel)
        {
            case NotificationChannel.Webhook:
                await ValidateUrlAsync(RequiredString(root, "url", channel), channel, cancellationToken);
                break;
            case NotificationChannel.Discord:
            case NotificationChannel.Slack:
                await ValidateUrlAsync(RequiredString(root, "webhookUrl", channel), channel, cancellationToken);
                break;
            case NotificationChannel.Telegram:
                if (!NotificationCredentialFormats.IsValidTelegramBotToken(RequiredString(root, "botToken", channel)))
                    throw new DomainException("Telegram bot token has an invalid format.");
                RequiredString(root, "chatId", channel);
                break;
            case NotificationChannel.Email:
                var host = RequiredString(root, "smtpHost", channel);
                var check = await outboundUrlPolicy.CheckHostAsync(host, cancellationToken);
                if (!check.Allowed)
                    throw new DomainException($"Email SMTP host is not allowed: {check.Reason}");
                break;
        }
    }

    private async Task ValidateUrlAsync(string value, NotificationChannel channel,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url))
            throw new DomainException($"{channel} URL must be an absolute URL.");

        var check = await outboundUrlPolicy.CheckUrlAsync(url, cancellationToken);
        if (!check.Allowed)
            throw new DomainException($"{channel} URL is not allowed: {check.Reason}");
    }

    private static string RequiredString(JsonElement root, string propertyName, NotificationChannel channel)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (property.Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.Value.GetString()))
                return property.Value.GetString()!.Trim();
        }

        throw new DomainException($"{channel} credentials require '{propertyName}'.");
    }

    private static NotificationTargetListItem ToListItem(NotificationTarget target)
    {
        return new NotificationTargetListItem(target.Id.Value, target.Name.Value, target.Channel, target.IsEnabled,
            target.Policy.MinimumSeverity, target.Policy.DeploymentEventsEnabled, target.Policy.BackupEventsEnabled,
            target.Policy.ServerEventsEnabled, target.CredentialsReferenceId.HasValue);
    }

    private static string NormalizeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException)
        {
            // Never echo the input: it may be the secret JSON.
            throw new DomainException("Notification target JSON is not valid.");
        }
    }
}
