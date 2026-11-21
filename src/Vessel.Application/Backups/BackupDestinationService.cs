using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Notifications;
using Vessel.Application.OutboundRequests;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Domain.Secrets;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Backups;

/// <summary>
///     Team-scoped backup destinations (S3-compatible storages). Reading requires <c>projects.read</c>; creating,
///     editing, validating and deleting require <c>secrets.write</c> because a destination carries credentials and
///     receives full database dumps. Credentials go to the secret vault and are never returned.
/// </summary>
public sealed class BackupDestinationService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    ISecretVault secretVault,
    ISecretRedactor redactor,
    IOutboundUrlPolicy outboundUrlPolicy,
    BackupDestinationConnector connector,
    IAuditWriter auditWriter,
    INotificationEventPublisher notifications,
    TimeProvider timeProvider)
{
    private static readonly SecretPolicy CredentialPolicy = new(false, false, false);

    public IReadOnlyList<BackupDestinationSummary> List(UserId actorUserId, TeamId teamId)
    {
        Require(actorUserId, teamId, VesselPermissions.ProjectsRead);
        return dbContext.BackupDestinations.Where(destination => destination.TeamId == teamId)
            .OrderBy(destination => destination.CreatedAt)
            .AsEnumerable()
            .Select(ToSummary)
            .ToArray();
    }

    public BackupDestinationSummary Get(UserId actorUserId, TeamId teamId, BackupDestinationId destinationId)
    {
        Require(actorUserId, teamId, VesselPermissions.ProjectsRead);
        return ToSummary(Find(teamId, destinationId));
    }

    public async Task<BackupDestinationSummary> CreateAsync(UserId actorUserId, TeamId teamId,
        CreateBackupDestinationRequest request, CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.SecretsWrite);
        var endpoint = BackupDestination.NormalizeEndpoint(request.Endpoint);
        await EnsureEndpointAllowedAsync(endpoint, cancellationToken);
        var credentials = BackupDestinationConnector.SerializeCredentials(request.AccessKeyId, request.SecretAccessKey);
        // Validate everything the aggregate checks before the secret is stored.
        BackupBucketName.Validate(request.Bucket);
        SecretReference secret = await secretVault.StoreAsync(teamId, SecretScope.Team,
            $"backup-destination:{Guid.NewGuid():N}", credentials, CredentialPolicy, new SecretTarget(),
            cancellationToken);
        var destination = BackupDestination.Create(teamId, new ResourceName(request.Name), endpoint, request.Region,
            request.Bucket, request.UsePathStyle, request.SkipTlsVerification, secret.Id, Now(),
            ToDescription(request.Description));
        await dbContext.BackupDestinationRepository.AddAsync(destination, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.BackupDestinationCreated,
            Target(destination), null,
            new Dictionary<string, object?>
            {
                ["endpoint"] = OutboundRequestRedaction.ForDisplay(new Uri(destination.Endpoint)),
                ["bucket"] = destination.Bucket,
                ["skipTlsVerification"] = destination.SkipTlsVerification
            }, cancellationToken);
        if (destination.SkipTlsVerification) await AuditTlsAsync(actorUserId, destination, cancellationToken);
        await ValidateAndRecordAsync(destination, cancellationToken);
        return ToSummary(destination);
    }

    public async Task<BackupDestinationSummary> UpdateAsync(UserId actorUserId, TeamId teamId,
        BackupDestinationId destinationId, UpdateBackupDestinationRequest request,
        CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.SecretsWrite);
        BackupDestination destination = Find(teamId, destinationId);
        var endpoint = request.Endpoint is null
            ? destination.Endpoint
            : BackupDestination.NormalizeEndpoint(request.Endpoint);
        await EnsureEndpointAllowedAsync(endpoint, cancellationToken);
        var tlsChanged = request.SkipTlsVerification is { } skip && skip != destination.SkipTlsVerification;
        destination.Configure(
            request.Name is null ? destination.Name : new ResourceName(request.Name),
            request.Description is null ? destination.Description : ToDescription(request.Description),
            endpoint,
            request.Region ?? destination.Region,
            request.Bucket ?? destination.Bucket,
            request.UsePathStyle ?? destination.UsePathStyle,
            request.SkipTlsVerification ?? destination.SkipTlsVerification,
            Now());
        if (request.AccessKeyId is not null || request.SecretAccessKey is not null)
        {
            var credentials = BackupDestinationConnector.SerializeCredentials(request.AccessKeyId ?? string.Empty,
                request.SecretAccessKey ?? string.Empty);
            await secretVault.ReplaceAsync(destination.CredentialsReferenceId, credentials, cancellationToken);
            destination.ReplaceCredentials(destination.CredentialsReferenceId, Now());
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.BackupDestinationUpdated, Target(destination),
            null,
            new Dictionary<string, object?>
            {
                ["credentialsReplaced"] = request.AccessKeyId is not null || request.SecretAccessKey is not null,
                ["skipTlsVerification"] = destination.SkipTlsVerification
            }, cancellationToken);
        if (tlsChanged) await AuditTlsAsync(actorUserId, destination, cancellationToken);
        await ValidateAndRecordAsync(destination, cancellationToken);
        return ToSummary(destination);
    }

    /// <summary>Runs the connection test now and records the usability state.</summary>
    public async Task<BackupDestinationSummary> ValidateAsync(UserId actorUserId, TeamId teamId,
        BackupDestinationId destinationId, CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.SecretsWrite);
        BackupDestination destination = Find(teamId, destinationId);
        await EnsureEndpointAllowedAsync(destination.Endpoint, cancellationToken);
        await ValidateAndRecordAsync(destination, cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.BackupDestinationValidated,
            Target(destination), null,
            new Dictionary<string, object?> { ["usable"] = destination.IsUsable }, cancellationToken);
        return ToSummary(destination);
    }

    /// <summary>
    ///     Deletes a destination: schedules are detached (a schedule that only had this destination gets its local copy
    ///     back), replicas keep their history without a destination reference, and the stored credentials are removed.
    ///     Objects already in the bucket are not touched.
    /// </summary>
    public async Task DeleteAsync(UserId actorUserId, TeamId teamId, BackupDestinationId destinationId,
        CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.SecretsWrite);
        BackupDestination destination = Find(teamId, destinationId);
        BackupSchedule[] schedules = dbContext.BackupSchedules.Where(schedule => schedule.TeamId == teamId)
            .AsEnumerable()
            .Where(schedule => schedule.DestinationIds.Contains(destination.Id))
            .ToArray();
        foreach (BackupSchedule schedule in schedules) schedule.DetachDestination(destination.Id, Now());
        dbContext.BackupDestinationRepository.Remove(destination);
        SecretReference? secret = dbContext.SecretReferences.SingleOrDefault(reference =>
            reference.Id == destination.CredentialsReferenceId && reference.TeamId == teamId);
        if (secret is not null) dbContext.SecretReferenceRepository.Remove(secret);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.BackupDestinationDeleted, Target(destination),
            null, new Dictionary<string, object?> { ["detachedSchedules"] = schedules.Length }, cancellationToken);
    }

    /// <summary>Hourly job body: re-tests every unusable destination (upstream RevalidateUnusableS3StoragesJob).</summary>
    public async Task<int> RevalidateUnusableAsync(CancellationToken cancellationToken = default)
    {
        BackupDestination[] unusable = dbContext.BackupDestinations.Where(destination => !destination.IsUsable)
            .ToArray();
        foreach (BackupDestination destination in unusable)
        {
            OutboundTargetCheck check = await outboundUrlPolicy.CheckUrlAsync(new Uri(destination.Endpoint),
                cancellationToken);
            if (!check.Allowed)
            {
                await RecordUnusableAsync(destination, $"Endpoint is not allowed: {check.Reason}", cancellationToken);
                continue;
            }

            await ValidateAndRecordAsync(destination, cancellationToken);
        }

        return unusable.Count(destination => destination.IsUsable);
    }

    private async Task ValidateAndRecordAsync(BackupDestination destination, CancellationToken cancellationToken)
    {
        string? failure = null;
        try
        {
            using IBackupDestinationClient client = await connector.ConnectAsync(destination, cancellationToken);
            await client.ValidateAsync(cancellationToken);
        }
        catch (BackupDestinationUnavailableException exception)
        {
            failure = exception.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = $"Connection test failed ({exception.GetType().Name}).";
        }

        if (failure is null)
        {
            destination.MarkUsable(Now());
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        await RecordUnusableAsync(destination, redactor.Redact(failure), cancellationToken);
    }

    private async Task RecordUnusableAsync(BackupDestination destination, string reason,
        CancellationToken cancellationToken)
    {
        var notify = destination.MarkUnusable(reason, Now());
        if (notify) destination.RecordUnusableNotified(Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        if (notify)
            await notifications.PublishAsync(destination.TeamId,
                OperationalNotifications.BackupDestinationUnusable(destination.Id.Value, destination.Name.Value),
                cancellationToken);
    }

    private async Task EnsureEndpointAllowedAsync(string endpoint, CancellationToken cancellationToken)
    {
        OutboundTargetCheck check = await outboundUrlPolicy.CheckUrlAsync(new Uri(endpoint), cancellationToken);
        if (!check.Allowed)
            throw new DomainException(
                $"Backup destination endpoint is not allowed: {check.Reason} Private or internal endpoints must be " +
                "added to the instance outbound allow-list.");
    }

    private Task AuditTlsAsync(UserId actorUserId, BackupDestination destination, CancellationToken cancellationToken)
    {
        return auditWriter.RecordAsync(destination.TeamId, actorUserId,
            AuditActions.BackupDestinationTlsVerificationChanged, Target(destination), null,
            new Dictionary<string, object?> { ["skipTlsVerification"] = destination.SkipTlsVerification },
            cancellationToken);
    }

    private BackupDestination Find(TeamId teamId, BackupDestinationId destinationId)
    {
        // Other teams' destinations are reported as not found so IDs cannot be probed.
        return dbContext.BackupDestinations.SingleOrDefault(destination =>
                   destination.Id == destinationId && destination.TeamId == teamId)
               ?? throw new InvalidOperationException("Backup destination was not found.");
    }

    private void Require(UserId actorUserId, TeamId teamId, string permission)
    {
        if (!authorization.HasPermission(actorUserId, teamId, permission))
            throw new UnauthorizedAccessException($"Missing required permission '{permission}'.");
    }

    private static Description? ToDescription(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : new Description(value);
    }

    private static AuditTarget Target(BackupDestination destination)
    {
        return new AuditTarget("backup-destination", destination.Id.Value.ToString("D"));
    }

    private DateTimeOffset Now()
    {
        return timeProvider.GetUtcNow();
    }

    public static BackupDestinationSummary ToSummary(BackupDestination destination)
    {
        return new BackupDestinationSummary(destination.Id.Value, destination.Name.Value,
            destination.Description?.Value, destination.Kind, destination.Endpoint, destination.Region,
            destination.Bucket, destination.UsePathStyle, destination.SkipTlsVerification, destination.IsUsable,
            destination.LastValidatedAt, destination.LastValidationError, destination.CreatedAt);
    }
}

public sealed class RevalidateBackupDestinationsJob(BackupDestinationService service)
{
    public const string RecurringJobId = "backup-destinations:revalidate";

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return service.RevalidateUnusableAsync(cancellationToken);
    }
}
