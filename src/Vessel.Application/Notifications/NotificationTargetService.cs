using System.Text.Json;
using Vessel.Application.Auditing;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Notifications;
using Vessel.Domain.Secrets;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Notifications;

public sealed class NotificationTargetService(
    IVesselDbContext dbContext,
    ISecretVault secretVault,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public IReadOnlyList<NotificationTargetListItem> ListTargets(TeamId teamId)
    {
        return dbContext.NotificationTargets
            .Where(target => target.TeamId == teamId)
            .OrderBy(target => target.Channel)
            .ThenBy(target => target.Name)
            .Select(target => new NotificationTargetListItem(
                target.Id.Value,
                target.Name.Value,
                target.Channel,
                target.IsEnabled,
                target.Policy.MinimumSeverity,
                target.Policy.DeploymentEventsEnabled,
                target.Policy.BackupEventsEnabled,
                target.Policy.ServerEventsEnabled))
            .ToArray();
    }

    public async Task<NotificationTargetListItem> UpsertTargetAsync(
        TeamId teamId,
        UserId actorUserId,
        UpsertNotificationTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var name = new ResourceName(request.Name);
        var policy = new NotificationDeliveryPolicy(request.MinimumSeverity, request.DeploymentEvents,
            request.BackupEvents, request.ServerEvents);
        var configurationJson = NormalizeJson(request.ConfigurationJson);

        var target = dbContext.NotificationTargets.FirstOrDefault(existing =>
            existing.TeamId == teamId && existing.Channel == request.Channel && existing.Name == name);

        SecretReferenceId? credentialsReferenceId = target?.CredentialsReferenceId;
        if (!string.IsNullOrWhiteSpace(request.SecretJson))
        {
            var secretJson = NormalizeJson(request.SecretJson);
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
                ["enabled"] = target.IsEnabled
            }, cancellationToken);

        return new NotificationTargetListItem(target.Id.Value, target.Name.Value, target.Channel, target.IsEnabled,
            target.Policy.MinimumSeverity, target.Policy.DeploymentEventsEnabled, target.Policy.BackupEventsEnabled,
            target.Policy.ServerEventsEnabled);
    }

    private static string NormalizeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";

        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }
}
