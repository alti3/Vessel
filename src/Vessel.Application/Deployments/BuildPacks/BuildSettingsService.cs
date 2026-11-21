using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using AppEntity = Vessel.Domain.Applications.Application;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Deployments.BuildPacks;

public sealed record ApplicationBuildSettingsDto(
    ApplicationBuildPack BuildPack,
    bool BuildPackDeprecated,
    ApplicationBuildPack RecommendedBuildPack,
    string BaseDirectory,
    string? DockerfilePath,
    string? InstallCommand,
    string? BuildCommand,
    string? StartCommand,
    string? PublishDirectory,
    bool IsSpa,
    string? RegistryImage,
    Guid? RegistryCredentialId,
    string? ComposeService);

/// <summary>
///     Partial update: null fields keep their current value; an empty string clears an optional text setting.
///     <see cref="RegistryCredentialId" /> of <see cref="Guid.Empty" /> clears the credential.
/// </summary>
public sealed record UpdateApplicationBuildSettingsRequest(
    ApplicationBuildPack? BuildPack = null,
    string? BaseDirectory = null,
    string? DockerfilePath = null,
    string? InstallCommand = null,
    string? BuildCommand = null,
    string? StartCommand = null,
    string? PublishDirectory = null,
    bool? IsSpa = null,
    string? RegistryImage = null,
    Guid? RegistryCredentialId = null,
    string? ComposeService = null);

/// <summary>
///     Build settings of an application (DEP-09): build pack, paths, Railpack/Nixpacks commands, Static publish
///     directory and SPA fallback, the registry repository production images are pushed to (DEP-06), and the public
///     Compose service. Changing them needs write permission on the application; a registry credential must belong to
///     the application's team. Every change is audited with before and after values (never credential secrets).
/// </summary>
public sealed class BuildSettingsService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public ApplicationBuildSettingsDto Get(UserId actorUserId, TeamId teamId, AppId applicationId)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId),
            VesselPermissions.ApplicationsRead);
        return ToDto(FindApplication(applicationId).BuildConfiguration);
    }

    public async Task<ApplicationBuildSettingsDto> UpdateAsync(UserId actorUserId, TeamId teamId,
        AppId applicationId, UpdateApplicationBuildSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId),
            VesselPermissions.ApplicationsWrite);
        AppEntity application = FindApplication(applicationId);
        BuildConfiguration before = application.BuildConfiguration;
        RegistryCredentialId? credential = request.RegistryCredentialId switch
        {
            null => before.RegistryCredentialId,
            { } id when id == Guid.Empty => null,
            { } id => new RegistryCredentialId(id)
        };
        if (credential is { } credentialId && request.RegistryCredentialId is not null &&
            !dbContext.RegistryCredentials.Any(item => item.Id == credentialId && item.TeamId == teamId))
            throw new UnauthorizedAccessException("Registry credential is outside the active team.");

        BuildConfiguration after = before with
        {
            BuildPack = request.BuildPack ?? before.BuildPack,
            BaseDirectory = request.BaseDirectory ?? before.BaseDirectory,
            DockerfilePath = Text(request.DockerfilePath, before.DockerfilePath),
            InstallCommand = Text(request.InstallCommand, before.InstallCommand),
            BuildCommand = Text(request.BuildCommand, before.BuildCommand),
            StartCommand = Text(request.StartCommand, before.StartCommand),
            PublishDirectory = Text(request.PublishDirectory, before.PublishDirectory),
            IsSpa = request.IsSpa ?? before.IsSpa,
            RegistryImage = Text(request.RegistryImage, before.RegistryImage),
            RegistryCredentialId = credential,
            ComposeService = Text(request.ComposeService, before.ComposeService)
        };
        if (after.RegistryImage is not null && after.BuildPack == ApplicationBuildPack.DockerCompose)
            throw new DomainException(
                "Registry push is not available for Docker Compose applications (upstream Coolify has no registry rollback for Compose either).");

        application.UpdateBuildConfiguration(after, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.ApplicationBuildSettingsUpdated,
            new AuditTarget("application", application.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["before"] = ToDto(before),
                ["after"] = ToDto(application.BuildConfiguration)
            }, cancellationToken);
        return ToDto(application.BuildConfiguration);
    }

    private static string? Text(string? requested, string? current)
    {
        if (requested is null) return current;
        return string.IsNullOrWhiteSpace(requested) ? null : requested;
    }

    private AppEntity FindApplication(AppId applicationId)
    {
        return dbContext.Applications.SingleOrDefault(item => item.Id == applicationId)
               ?? throw new InvalidOperationException("Application was not found.");
    }

    public static ApplicationBuildSettingsDto ToDto(BuildConfiguration configuration)
    {
        return new ApplicationBuildSettingsDto(configuration.BuildPack,
            ApplicationBuildPacks.IsDeprecated(configuration.BuildPack), ApplicationBuildPacks.Recommended,
            configuration.BaseDirectory, configuration.DockerfilePath, configuration.InstallCommand,
            configuration.BuildCommand, configuration.StartCommand, configuration.PublishDirectory,
            configuration.IsSpa, configuration.RegistryImage, configuration.RegistryCredentialId?.Value,
            configuration.ComposeService);
    }
}
