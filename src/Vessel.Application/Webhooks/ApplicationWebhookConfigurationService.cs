using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Git;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Secrets;
using Vessel.Domain.Webhooks;
using AppId = Vessel.Domain.ApplicationId;
using Environment = Vessel.Domain.Projects.Environment;

namespace Vessel.Application.Webhooks;

public sealed class ApplicationWebhookConfigurationService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    ISecretVault secretVault,
    IGitClient git,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public IReadOnlyList<ApplicationWebhookConfigurationSummary> List(
        UserId actorUserId,
        TeamId teamId,
        AppId applicationId)
    {
        RequireApplication(actorUserId, teamId, applicationId, VesselPermissions.ApplicationsRead);

        return dbContext.ApplicationWebhookConfigurations
            .Where(configuration => configuration.ApplicationId == applicationId)
            .Select(configuration => new ApplicationWebhookConfigurationSummary(
                configuration.Id.Value,
                configuration.ApplicationId.Value,
                configuration.Provider,
                configuration.IsEnabled,
                configuration.SecretReferenceId.Value,
                configuration.CreatedAt,
                configuration.LastRotatedAt))
            .ToArray();
    }

    public async Task<ApplicationWebhookConfigurationSummary> ConfigureAsync(
        UserId actorUserId,
        TeamId teamId,
        AppId applicationId,
        ConfigureApplicationWebhookRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireApplication(actorUserId, teamId, applicationId, VesselPermissions.ApplicationsWrite);
        // SEC-07: new or changed manual webhook secrets need at least 16 characters (existing rows keep working).
        var secret = new WebhookSecret(request.Secret);

        (ProjectId projectId, EnvironmentId environmentId) = ApplicationProject(applicationId);
        ApplicationWebhookConfiguration? existing = dbContext.ApplicationWebhookConfigurations
            .SingleOrDefault(configuration =>
                configuration.ApplicationId == applicationId && configuration.Provider == request.Provider);

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (existing is null)
        {
            SecretReference reference = await secretVault.StoreAsync(
                teamId,
                SecretScope.Application,
                $"webhook:{request.Provider.ToString().ToLowerInvariant()}",
                secret.Value,
                new SecretPolicy(false, false, false),
                new SecretTarget(projectId, environmentId, ApplicationId: applicationId),
                cancellationToken);
            existing = ApplicationWebhookConfiguration.Create(applicationId, request.Provider, reference.Id, now);
            existing.SetEnabled(request.Enabled, now);
            await dbContext.ApplicationWebhookConfigurationRepository.AddAsync(existing, cancellationToken);
        }
        else
        {
            await secretVault.ReplaceAsync(existing.SecretReferenceId, secret.Value, cancellationToken);
            existing.SetEnabled(request.Enabled, now);
            existing.ReplaceSecret(existing.SecretReferenceId, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.WebhookConfigured,
            new AuditTarget("application", applicationId.Value.ToString("D")), null,
            new Dictionary<string, object?>
            { ["provider"] = request.Provider.ToString(), ["enabled"] = request.Enabled },
            cancellationToken);

        return new ApplicationWebhookConfigurationSummary(
            existing.Id.Value,
            existing.ApplicationId.Value,
            existing.Provider,
            existing.IsEnabled,
            existing.SecretReferenceId.Value,
            existing.CreatedAt,
            existing.LastRotatedAt);
    }

    public async Task<IReadOnlyList<GitRepositoryRefSummary>> ListRefsAsync(
        UserId actorUserId,
        TeamId teamId,
        AppId applicationId,
        CancellationToken cancellationToken = default)
    {
        RequireApplication(actorUserId, teamId, applicationId, VesselPermissions.ApplicationsRead);
        Domain.Applications.Application application =
            dbContext.Applications.Single(application => application.Id == applicationId);
        IReadOnlyList<GitRepositoryRef> refs =
            await git.ListRefsAsync(new Uri(application.GitSource.RepositoryUrl.Value), cancellationToken);
        return refs.Select(item => new GitRepositoryRefSummary(item.Name, item.Sha, item.IsTag)).ToArray();
    }

    private void RequireApplication(UserId actorUserId, TeamId teamId, AppId applicationId, string permission)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId), permission);
    }

    private (ProjectId ProjectId, EnvironmentId EnvironmentId) ApplicationProject(AppId applicationId)
    {
        Domain.Applications.Application application =
            dbContext.Applications.Single(application => application.Id == applicationId);
        Environment environment =
            dbContext.Environments.Single(environment => environment.Id == application.EnvironmentId);
        return (environment.ProjectId, environment.Id);
    }
}
