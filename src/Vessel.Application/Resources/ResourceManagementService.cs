using Microsoft.Extensions.Options;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.ManagedServices;
using Vessel.Application.Monitoring;
using Vessel.Application.Persistence;
using Vessel.Application.Proxy;
using Vessel.Application.Proxy.Domains;
using Vessel.Application.Security;
using Vessel.Application.Sources;
using Vessel.Application.Ssh;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Databases;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.Projects;
using Vessel.Domain.Registries;
using Vessel.Domain.Secrets;
using Vessel.Domain.Servers;
using Vessel.Domain.Sources;
using Vessel.Domain.ValueObjects;
using ApplicationId = Vessel.Domain.ApplicationId;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.Application.Resources;

public sealed class ResourceManagementService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    ISecretVault secretVault,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    ServerHealthPollingService serverHealth,
    IOptions<SshOptions>? sshOptions = null)
{
    public IReadOnlyList<ProjectSummary> ListProjects(UserId actorUserId, TeamId teamId)
    {
        RequireSomewhere(actorUserId, teamId, VesselPermissions.ProjectsRead);
        AccessVisibility visibility = authorization.GetVisibility(actorUserId, teamId);

        return dbContext.Projects
            .Where(project => project.TeamId == teamId)
            .Select(project => new ProjectSummary(
                project.Id.Value,
                project.Name.Value,
                project.Description == null ? null : project.Description.Value.Value,
                project.IsArchived,
                dbContext.Environments.Count(environment => environment.ProjectId == project.Id)))
            .AsEnumerable()
            .Where(project => visibility.CanSeeProject(project.Id))
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public ProjectDetails GetProject(UserId actorUserId, TeamId teamId, ProjectId projectId)
    {
        RequireProjectOpen(actorUserId, teamId, projectId);
        Project project = dbContext.Projects.Single(project => project.Id == projectId);

        return new ProjectDetails(
            project.Id.Value,
            project.Name.Value,
            project.Description == null ? null : project.Description.Value.Value,
            project.IsArchived,
            ListEnvironments(actorUserId, teamId, projectId));
    }

    public async Task<ProjectDetails> CreateProjectAsync(
        UserId actorUserId,
        TeamId teamId,
        CreateProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        // Creating a project needs team-wide write; project-scoped grants never suffice (IAM-05).
        Require(actorUserId, teamId, VesselPermissions.ProjectsWrite);
        DateTimeOffset now = Now();

        var project = Project.Create(teamId, new ResourceName(request.Name), now, ToDescription(request.Description));
        var production = EnvironmentEntity.CreateProduction(project.Id, now);
        await dbContext.ProjectRepository.AddAsync(project, cancellationToken);
        await dbContext.EnvironmentRepository.AddAsync(production, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.ProjectCreated, "project", project.Id.Value,
            cancellationToken);

        return GetProject(actorUserId, teamId, project.Id);
    }

    public async Task<ProjectDetails> UpdateProjectAsync(
        UserId actorUserId,
        TeamId teamId,
        ProjectId projectId,
        UpdateProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireProject(actorUserId, teamId, projectId, VesselPermissions.ProjectsWrite);
        Project project = await GetRequiredAsync(dbContext.ProjectRepository, projectId, cancellationToken);
        project.UpdateDetails(new ResourceName(request.Name), ToDescription(request.Description), Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.ProjectUpdated, "project", project.Id.Value,
            cancellationToken);

        return GetProject(actorUserId, teamId, projectId);
    }

    public async Task ArchiveProjectAsync(
        UserId actorUserId,
        TeamId teamId,
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        RequireProject(actorUserId, teamId, projectId, VesselPermissions.ProjectsDelete);
        Project project = await GetRequiredAsync(dbContext.ProjectRepository, projectId, cancellationToken);
        project.Archive(Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.ProjectArchived, "project", project.Id.Value,
            cancellationToken);
    }

    public IReadOnlyList<EnvironmentSummary> ListEnvironments(UserId actorUserId, TeamId teamId, ProjectId projectId)
    {
        RequireProjectOpen(actorUserId, teamId, projectId);
        AccessVisibility visibility = authorization.GetVisibility(actorUserId, teamId);

        return dbContext.Environments
            .Where(environment => environment.ProjectId == projectId)
            .OrderBy(environment => environment.Kind)
            .ThenBy(environment => environment.Name)
            .Select(environment => new EnvironmentSummary(
                environment.Id.Value,
                environment.ProjectId.Value,
                environment.Name.Value,
                environment.Kind,
                environment.Description == null ? null : environment.Description.Value.Value))
            .AsEnumerable()
            .Where(environment => visibility.CanSeeEnvironment(environment.Id))
            .ToArray();
    }

    public async Task<EnvironmentSummary> CreateEnvironmentAsync(
        UserId actorUserId,
        TeamId teamId,
        CreateEnvironmentRequest request,
        CancellationToken cancellationToken = default)
    {
        var projectId = new ProjectId(request.ProjectId);
        RequireProject(actorUserId, teamId, projectId, VesselPermissions.ProjectsWrite);
        var environment = EnvironmentEntity.Create(projectId, new Slug(request.Name), request.Kind, Now());
        environment.Update(new Slug(request.Name), request.Kind, ToDescription(request.Description), Now());
        await dbContext.EnvironmentRepository.AddAsync(environment, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.EnvironmentCreated, "environment", environment.Id.Value,
            cancellationToken);

        return ToSummary(environment);
    }

    public async Task<EnvironmentSummary> UpdateEnvironmentAsync(
        UserId actorUserId,
        TeamId teamId,
        EnvironmentId environmentId,
        UpdateEnvironmentRequest request,
        CancellationToken cancellationToken = default)
    {
        EnvironmentEntity environment =
            await GetRequiredAsync(dbContext.EnvironmentRepository, environmentId, cancellationToken);
        RequireProject(actorUserId, teamId, environment.ProjectId, VesselPermissions.ProjectsWrite);
        environment.Update(new Slug(request.Name), request.Kind, ToDescription(request.Description), Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.EnvironmentUpdated, "environment", environment.Id.Value,
            cancellationToken);

        return ToSummary(environment);
    }

    public async Task DeleteEnvironmentAsync(
        UserId actorUserId,
        TeamId teamId,
        EnvironmentId environmentId,
        CancellationToken cancellationToken = default)
    {
        EnvironmentEntity environment =
            await GetRequiredAsync(dbContext.EnvironmentRepository, environmentId, cancellationToken);
        RequireProject(actorUserId, teamId, environment.ProjectId, VesselPermissions.ProjectsDelete);
        if (environment.Kind == EnvironmentKind.Production)
            throw new InvalidOperationException("Production environments cannot be deleted.");
        if (dbContext.Applications.Any(application => application.EnvironmentId == environmentId)
            || dbContext.DatabaseResources.Any(database => database.EnvironmentId == environmentId))
            throw new InvalidOperationException("Environments with resources cannot be deleted.");

        dbContext.EnvironmentRepository.Remove(environment);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.EnvironmentDeleted, "environment", environment.Id.Value,
            cancellationToken);
    }

    public IReadOnlyList<ServerSummary> ListServers(UserId actorUserId, TeamId teamId)
    {
        RequireSomewhere(actorUserId, teamId, VesselPermissions.ServersRead);
        AccessVisibility visibility = authorization.GetVisibility(actorUserId, teamId);

        // Scoped actors only see the servers that run a resource they can reach (Coolify v4.7 parity).
        return dbContext.Servers
            .Where(server => server.TeamId == teamId)
            .AsEnumerable()
            .Where(server => visibility.CanSeeServer(server.Id.Value))
            .OrderBy(server => server.Name.Value, StringComparer.Ordinal)
            .Select(ToSummary)
            .ToArray();
    }

    public async Task<ServerSummary> CreateServerAsync(
        UserId actorUserId,
        TeamId teamId,
        CreateServerRequest request,
        CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.ServersWrite);
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, request.ConnectionType);
        DateTimeOffset now = Now();
        var server = Server.Create(teamId, new ResourceName(request.Name),
            ToServerAddress(request.Host, request.Port, request.User),
            request.ConnectionType, request.Runtime, now, ToDescription(request.Description));
        server.UpdateSettings(server.Name, server.Description, server.Address, request.ConnectionType, request.Runtime,
            request.Labels, now);
        if (request.ConnectionType == ServerConnectionType.Ssh)
        {
            if (!SshTargetValidator.IsSafeHost(request.Host) ||
                (!string.IsNullOrWhiteSpace(request.User) && !SshTargetValidator.IsSafeUser(request.User)))
                throw new DomainException("Server address or user contains characters that are not allowed for SSH.");
            // New servers start at the instance host key checking mode (enforce by default).
            server.SetHostKeyChecking(sshOptions?.Value.ResolveInstanceMode() ?? SshHostKeyCheckingMode.Enforce, now);
        }

        if (request.SshKeyId.HasValue)
        {
            var keyId = new SshKeyId(request.SshKeyId.Value);
            // Only keys of the same team can be selected; another team's key looks like a missing key.
            if (!dbContext.SshKeys.Any(key => key.Id == keyId && key.TeamId == teamId))
                throw new DomainException("SSH key was not found.");
            server.AssignSshKey(keyId, now);
        }

        await dbContext.ServerRepository.AddAsync(server, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.ServerCreated, "server", server.Id.Value, cancellationToken);

        return ToSummary(server);
    }

    public async Task<ServerConnectivityResult> TestServerConnectivityAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersWrite);
        Server server = await GetRequiredAsync(dbContext.ServerRepository, serverId, cancellationToken);
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);

        // A real probe over the server's own connection; the snapshot records measured state only.
        ServerHealthPollingResult probe =
            await serverHealth.PollAsync(actorUserId, teamId, serverId, cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.ServerConnectivityChecked, "server", server.Id.Value,
            cancellationToken);

        return new ServerConnectivityResult(server.Id.Value, probe.Status == ServerStatus.Reachable, server.Runtime,
            probe.Reason ?? probe.Status.ToString(), Now());
    }

    public IReadOnlyList<ServerStatusSnapshotSummary> ListServerSnapshots(UserId actorUserId, TeamId teamId,
        ServerId serverId)
    {
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersRead);

        return dbContext.ServerStatusSnapshots
            .Where(snapshot => snapshot.ServerId == serverId)
            .OrderByDescending(snapshot => snapshot.CreatedAt)
            .Take(20)
            .Select(snapshot => new ServerStatusSnapshotSummary(snapshot.Id.Value, snapshot.ServerId.Value,
                snapshot.Status,
                snapshot.CpuLoadPercent, snapshot.MemoryUsedBytes, snapshot.DiskUsedBytes, snapshot.RunningContainers,
                snapshot.ProxyHealthy, snapshot.CertificatesHealthy, snapshot.CreatedAt))
            .ToArray();
    }

    public IReadOnlyList<ApplicationSummary> ListApplications(UserId actorUserId, TeamId teamId)
    {
        RequireSomewhere(actorUserId, teamId, VesselPermissions.ApplicationsRead);
        AccessVisibility visibility = authorization.GetVisibility(actorUserId, teamId);
        return ApplicationsForTeam(teamId)
            .AsEnumerable()
            .Where(application => visibility.CanSeeResource(application.EnvironmentId.Value, application.Id.Value))
            .OrderBy(application => application.Name.Value, StringComparer.Ordinal)
            .Select(ToSummary)
            .ToArray();
    }

    public async Task<ApplicationSummary> CreateApplicationAsync(
        UserId actorUserId,
        TeamId teamId,
        CreateApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        var projectId = new ProjectId(request.ProjectId);
        var environmentId = new EnvironmentId(request.EnvironmentId);
        var serverId = new ServerId(request.ServerId);
        RequireProject(actorUserId, teamId, projectId, VesselPermissions.ApplicationsWrite);
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ApplicationsWrite);
        RequireHostLocalTargetAllowed(actorUserId, serverId);
        EnsureEnvironmentBelongsToProject(environmentId, projectId);

        DateTimeOffset now = Now();
        var application = Domain.Applications.Application.Create(environmentId, serverId,
            new ResourceName(request.Name),
            // SEC-10: repository URL (no embedded credentials) and branch are validated at the API/UI boundary.
            GitSource.FromInput(request.RepositoryUrl, request.Branch),
            BuildConfig(request.BuildPack, request.BaseDirectory, request.DockerfilePath), now);
        // DEP-08: a private Git source must be usable by the team and accept the repository host.
        GitSourceConnection? source = GitSourceSelection.Resolve(dbContext, teamId, request.SourceId,
            application.GitSource.RepositoryUrl);
        application.AssignSource(source?.Id, now);
        application.UpdateSettings(application.Name, ToDescription(request.Description), application.GitSource,
            application.BuildConfiguration, RuntimeConfig(request.ExposedPort), DeploymentSettings.Default, now);
        foreach (var domain in request.Domains.Where(domain => !string.IsNullOrWhiteSpace(domain)))
        {
            // Same rules as the domains API: URL/host forms, and no silent claim of a route another resource uses.
            RouteAddress address = RouteAddress.Parse(domain);
            IReadOnlyList<DomainConflict> conflicts = DomainConflicts.Find(dbContext, teamId, address.WithoutPort(),
                RouteOwnerKind.Application, application.Id.Value);
            if (conflicts.Count > 0) throw new DomainException(DomainConflicts.Describe(conflicts));
            application.AddDomain(address, now);
        }

        await dbContext.ApplicationRepository.AddAsync(application, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.ApplicationCreated, "application", application.Id.Value,
            cancellationToken);

        return ToSummary(application);
    }

    public IReadOnlyList<DatabaseSummary> ListDatabases(UserId actorUserId, TeamId teamId)
    {
        RequireSomewhere(actorUserId, teamId, VesselPermissions.ProjectsRead);
        AccessVisibility visibility = authorization.GetVisibility(actorUserId, teamId);
        return DatabasesForTeam(teamId)
            .AsEnumerable()
            .Where(database => visibility.CanSeeResource(database.EnvironmentId.Value, database.Id.Value))
            .OrderBy(database => database.Name.Value, StringComparer.Ordinal)
            .Select(ToSummary)
            .ToArray();
    }

    public async Task<DatabaseSummary> CreateDatabaseAsync(
        UserId actorUserId,
        TeamId teamId,
        CreateDatabaseRequest request,
        CancellationToken cancellationToken = default)
    {
        var projectId = new ProjectId(request.ProjectId);
        var environmentId = new EnvironmentId(request.EnvironmentId);
        var serverId = new ServerId(request.ServerId);
        RequireProject(actorUserId, teamId, projectId, VesselPermissions.ProjectsWrite);
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ProjectsWrite);
        RequireHostLocalTargetAllowed(actorUserId, serverId);
        EnsureEnvironmentBelongsToProject(environmentId, projectId);
        // New databases always run a pinned image (engine default, a tag for the engine image, or an explicit image).
        // A bare "latest" version from older clients means "use the pinned default".
        DatabaseImage image = DatabaseEngineProfiles.ResolveNewImage(request.Engine, request.Image,
            string.Equals(request.Version, "latest", StringComparison.OrdinalIgnoreCase) ? null : request.Version,
            request.RedisFlavor);
        var mountPath = string.IsNullOrWhiteSpace(request.MountPath)
            ? DatabaseEngineProfiles.DefaultMountPath(request.Engine, image)
            : request.MountPath;
        var volumeName = string.IsNullOrWhiteSpace(request.VolumeName) ? "data" : request.VolumeName;
        EnsureComposeSafeCredentials(request.Credentials);
        SecretReference secret = await secretVault.StoreAsync(teamId, SecretScope.Database,
            $"{request.Name}:credentials",
            request.Credentials, SecretPolicy.Default, new SecretTarget(projectId, environmentId, serverId),
            cancellationToken);
        var database = DatabaseResource.Create(environmentId, serverId, new ResourceName(request.Name), request.Engine,
            new VersionLabel(image.Tag ?? image.Digest ?? "pinned"), new StorageConfiguration(volumeName, mountPath),
            secret.Id, Now(), image);
        await dbContext.DatabaseResourceRepository.AddAsync(database, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.DatabaseCreated, "database", database.Id.Value,
            cancellationToken);

        return ToSummary(database);
    }

    private static void EnsureComposeSafeCredentials(string credentials)
    {
        try
        {
            ComposeEnvironmentFile.Quote(credentials);
        }
        catch (ArgumentException exception)
        {
            throw new DomainException(exception.Message.Split(" (Parameter", 2)[0]);
        }
    }

    public IReadOnlyList<EnvironmentVariableSummary> ListEnvironmentVariables(UserId actorUserId, TeamId teamId)
    {
        Require(actorUserId, teamId, VesselPermissions.ProjectsRead);
        // Sensitive-response policy (IAM-04): values are returned only to actors (and tokens) holding read:sensitive.
        var canReveal = authorization.CanReadSensitive(actorUserId, teamId);

        return dbContext.EnvironmentVariables
            .Where(variable => variable.TeamId == teamId)
            .OrderBy(variable => variable.TargetType)
            .ThenBy(variable => variable.Key)
            .Select(variable => new EnvironmentVariableSummary(
                variable.Id.Value,
                variable.TargetType,
                variable.Key.Value,
                variable.ValueKind,
                variable.ValueKind == EnvironmentVariableValueKind.Secret || !canReveal
                    ? "********"
                    : variable.PlainValue ?? string.Empty,
                canReveal && variable.ValueKind == EnvironmentVariableValueKind.Secret,
                variable.IsBuildTime,
                variable.IsRuntime,
                variable.IsPreview,
                variable.IsLiteral,
                variable.IsMultiline,
                variable.Comment))
            .ToArray();
    }

    public async Task<EnvironmentVariableSummary> CreateEnvironmentVariableAsync(
        UserId actorUserId,
        TeamId teamId,
        CreateEnvironmentVariableRequest request,
        CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.SecretsWrite);
        SecretReferenceId? secretReferenceId = null;
        var plainValue = request.Value;
        SecretTarget target = ResolveTarget(teamId, request);
        if (request.ValueKind == EnvironmentVariableValueKind.Secret)
        {
            SecretReference secret = await secretVault.StoreAsync(teamId, ToSecretScope(request.TargetType),
                request.Key,
                request.Value, new SecretPolicy(false, request.IsBuildTime, request.IsRuntime), target,
                cancellationToken);
            secretReferenceId = secret.Id;
            plainValue = null;
        }

        var variable = EnvironmentVariable.Create(teamId, request.TargetType, new EnvironmentVariableKey(request.Key),
            request.ValueKind, plainValue, secretReferenceId, request.IsBuildTime, request.IsRuntime, request.IsPreview,
            request.IsLiteral, request.IsMultiline, request.Comment, Now());
        ApplyTarget(variable, request, target);
        // DEP-10: new variables go to the end of their scope's page order (deployments write variables in that order).
        var scopeCount = dbContext.EnvironmentVariables.Count(item =>
            item.TeamId == teamId && item.TargetType == variable.TargetType && item.ProjectId == variable.ProjectId &&
            item.EnvironmentId == variable.EnvironmentId && item.ServerId == variable.ServerId &&
            item.ApplicationId == variable.ApplicationId && item.DatabaseResourceId == variable.DatabaseResourceId);
        variable.Reorder(scopeCount, Now());
        await dbContext.EnvironmentVariableRepository.AddAsync(variable, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.EnvironmentVariableCreated, "environment-variable",
            variable.Id.Value, cancellationToken);

        return ListEnvironmentVariables(actorUserId, teamId).Single(item => item.Id == variable.Id.Value);
    }

    public async Task<string> RevealEnvironmentVariableAsync(
        UserId actorUserId,
        TeamId teamId,
        EnvironmentVariableId variableId,
        CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.SecretsRead);
        EnvironmentVariable variable =
            await GetRequiredAsync(dbContext.EnvironmentVariableRepository, variableId, cancellationToken);
        if (variable.TeamId != teamId)
            throw new UnauthorizedAccessException("Environment variable is outside the active team.");
        if (variable.ValueKind != EnvironmentVariableValueKind.Secret || !variable.SecretReferenceId.HasValue)
            return variable.PlainValue ?? string.Empty;

        return await secretVault.RevealAsync(actorUserId, teamId, variable.SecretReferenceId.Value, cancellationToken);
    }

    public async Task<RegistryCredentialSummary> CreateRegistryCredentialAsync(
        UserId actorUserId,
        TeamId teamId,
        CreateRegistryCredentialRequest request,
        CancellationToken cancellationToken = default)
    {
        Require(actorUserId, teamId, VesselPermissions.SecretsWrite);
        SecretReference secret = await secretVault.StoreAsync(teamId, SecretScope.Team,
            $"{request.Registry}:{request.Username}",
            request.Password, SecretPolicy.Default, new SecretTarget(), cancellationToken);
        var credential = RegistryCredential.Create(teamId, new ResourceName(request.Name), request.Registry,
            request.Username, secret.Id, Now());
        await dbContext.RegistryCredentialRepository.AddAsync(credential, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.RegistryCredentialCreated, "registry-credential",
            credential.Id.Value, cancellationToken);

        return new RegistryCredentialSummary(credential.Id.Value, credential.Name.Value, credential.Registry,
            credential.Username, credential.PasswordReferenceId.Value);
    }

    private void Require(UserId actorUserId, TeamId teamId, string permission)
    {
        if (!authorization.HasPermission(actorUserId, teamId, permission))
            throw new UnauthorizedAccessException($"Missing required permission '{permission}'.");
    }

    /// <summary>List gate: the permission on any scope; results are then filtered by visibility.</summary>
    private void RequireSomewhere(UserId actorUserId, TeamId teamId, string permission)
    {
        if (!authorization.HasPermissionSomewhere(actorUserId, teamId, permission))
            throw new UnauthorizedAccessException($"Missing required permission '{permission}'.");
    }

    /// <summary>
    ///     Opening a project needs Read on it, or a grant on one of its environments or resources (the project then
    ///     shows only the children the actor can see).
    /// </summary>
    private void RequireProjectOpen(UserId actorUserId, TeamId teamId, ProjectId projectId)
    {
        if (authorization.IsAuthorized(actorUserId, teamId, ResourceRef.Project(projectId),
                VesselPermissions.ProjectsRead))
            return;

        var inTeam = dbContext.Projects.Any(project => project.Id == projectId && project.TeamId == teamId);
        if (inTeam && authorization.HasPermissionSomewhere(actorUserId, teamId, VesselPermissions.ProjectsRead)
                   && authorization.GetVisibility(actorUserId, teamId).CanSeeProject(projectId.Value))
            return;

        RequireProject(actorUserId, teamId, projectId, VesselPermissions.ProjectsRead);
    }

    private void RequireProject(UserId actorUserId, TeamId teamId, ProjectId projectId, string permission)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Project(projectId), permission);
    }

    private void RequireServer(UserId actorUserId, TeamId teamId, ServerId serverId, string permission)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), permission);
    }

    private void RequireHostLocalTargetAllowed(UserId actorUserId, ServerId serverId)
    {
        ServerConnectionType? connectionType = dbContext.Servers
            .Where(server => server.Id == serverId)
            .Select(server => (ServerConnectionType?)server.ConnectionType)
            .SingleOrDefault();
        if (connectionType.HasValue)
            HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, connectionType.Value);
    }

    private DateTimeOffset Now()
    {
        return timeProvider.GetUtcNow();
    }

    private async Task AuditAsync(
        TeamId teamId,
        UserId actorUserId,
        string action,
        string targetType,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        await auditWriter.RecordAsync(teamId, actorUserId, action, new AuditTarget(targetType, targetId.ToString("D")),
            null, new Dictionary<string, object?>(), cancellationToken);
    }

    private void EnsureEnvironmentBelongsToProject(EnvironmentId environmentId, ProjectId projectId)
    {
        if (!dbContext.Environments.Any(environment =>
                environment.Id == environmentId && environment.ProjectId == projectId))
            throw new DomainException("Environment does not belong to the selected project.");
    }

    private SecretTarget ResolveTarget(TeamId teamId, CreateEnvironmentVariableRequest request)
    {
        ProjectId? projectId = request.ProjectId.HasValue ? new ProjectId(request.ProjectId.Value) : null;
        EnvironmentId? environmentId =
            request.EnvironmentId.HasValue ? new EnvironmentId(request.EnvironmentId.Value) : null;
        ServerId? serverId = request.ServerId.HasValue ? new ServerId(request.ServerId.Value) : null;
        ApplicationId? applicationId =
            request.ApplicationId.HasValue ? new ApplicationId(request.ApplicationId.Value) : null;
        DatabaseResourceId? databaseId = request.DatabaseResourceId.HasValue
            ? new DatabaseResourceId(request.DatabaseResourceId.Value)
            : null;

        // Every referenced target must be owned by the active team; a variable can never be attached to (or leak
        // into) another team's resource.
        EnsureOwnedByTeam(teamId, projectId.HasValue ? ResourceRef.Project(projectId.Value) : null);
        EnsureOwnedByTeam(teamId, environmentId.HasValue ? ResourceRef.Environment(environmentId.Value) : null);
        EnsureOwnedByTeam(teamId, serverId.HasValue ? ResourceRef.Server(serverId.Value) : null);
        EnsureOwnedByTeam(teamId, applicationId.HasValue ? ResourceRef.Application(applicationId.Value) : null);
        EnsureOwnedByTeam(teamId, databaseId.HasValue ? ResourceRef.Database(databaseId.Value) : null);
        return new SecretTarget(projectId, environmentId, serverId, applicationId, databaseId);
    }

    private void EnsureOwnedByTeam(TeamId teamId, ResourceRef? resource)
    {
        if (resource.HasValue && authorization.ResolveOwningTeam(resource.Value) != teamId)
            throw new UnauthorizedAccessException($"{resource.Value.Kind} is outside the active team.");
    }

    private static void ApplyTarget(EnvironmentVariable variable, CreateEnvironmentVariableRequest request,
        SecretTarget target)
    {
        switch (request.TargetType)
        {
            case EnvironmentVariableTargetType.Team:
                break;
            case EnvironmentVariableTargetType.Project:
                variable.TargetProject(target.ProjectId ?? throw new DomainException("Project target is required."));
                break;
            case EnvironmentVariableTargetType.Environment:
                variable.TargetEnvironment(
                    target.ProjectId ?? throw new DomainException("Project target is required."),
                    target.EnvironmentId ?? throw new DomainException("Environment target is required."));
                break;
            case EnvironmentVariableTargetType.Server:
                variable.TargetServer(target.ServerId ?? throw new DomainException("Server target is required."));
                break;
            case EnvironmentVariableTargetType.Application:
                variable.TargetApplication(
                    target.ProjectId ?? throw new DomainException("Project target is required."),
                    target.EnvironmentId ?? throw new DomainException("Environment target is required."),
                    target.ApplicationId ?? throw new DomainException("Application target is required."));
                break;
            case EnvironmentVariableTargetType.Database:
                variable.TargetDatabase(
                    target.ProjectId ?? throw new DomainException("Project target is required."),
                    target.EnvironmentId ?? throw new DomainException("Environment target is required."),
                    target.DatabaseResourceId ?? throw new DomainException("Database target is required."));
                break;
            default:
                throw new DomainException("Unsupported environment variable target.");
        }
    }

    private static SecretScope ToSecretScope(EnvironmentVariableTargetType targetType)
    {
        return targetType switch
        {
            EnvironmentVariableTargetType.Team => SecretScope.Team,
            EnvironmentVariableTargetType.Project => SecretScope.Project,
            EnvironmentVariableTargetType.Environment => SecretScope.Environment,
            EnvironmentVariableTargetType.Server => SecretScope.Server,
            EnvironmentVariableTargetType.Application => SecretScope.Application,
            EnvironmentVariableTargetType.Database => SecretScope.Database,
            _ => SecretScope.Team
        };
    }

    private static BuildConfiguration BuildConfig(ApplicationBuildPack buildPack, string baseDirectory,
        string? dockerfilePath)
    {
        return new BuildConfiguration(buildPack, string.IsNullOrWhiteSpace(baseDirectory) ? "/" : baseDirectory,
            dockerfilePath, null, null, null, null);
    }

    private static RuntimeConfiguration RuntimeConfig(int? exposedPort)
    {
        return new RuntimeConfiguration(exposedPort.HasValue ? new PortNumber(exposedPort.Value) : null,
            ResourceLimits.Unbounded, true, "/");
    }

    private IQueryable<Domain.Applications.Application> ApplicationsForTeam(TeamId teamId)
    {
        IQueryable<EnvironmentEntity> teamEnvironments = dbContext.Environments
            .Join(dbContext.Projects.Where(project => project.TeamId == teamId),
                environment => environment.ProjectId,
                project => project.Id,
                (environment, _) => environment);

        return dbContext.Applications.Join(teamEnvironments, application => application.EnvironmentId,
            environment => environment.Id, (application, _) => application);
    }

    private IQueryable<DatabaseResource> DatabasesForTeam(TeamId teamId)
    {
        IQueryable<EnvironmentEntity> teamEnvironments = dbContext.Environments
            .Join(dbContext.Projects.Where(project => project.TeamId == teamId),
                environment => environment.ProjectId,
                project => project.Id,
                (environment, _) => environment);

        return dbContext.DatabaseResources.Join(teamEnvironments, database => database.EnvironmentId,
            environment => environment.Id, (database, _) => database);
    }

    private ApplicationSummary ToSummary(Domain.Applications.Application application)
    {
        ProjectId projectId = dbContext.Environments.Where(environment => environment.Id == application.EnvironmentId)
            .Select(environment => environment.ProjectId).Single();

        return new ApplicationSummary(application.Id.Value, application.Name.Value,
            application.Description == null ? null : application.Description.Value.Value, projectId.Value,
            application.EnvironmentId.Value, application.ServerId.Value, application.GitSource.RepositoryUrl.Value,
            application.GitSource.Branch, application.BuildConfiguration.BuildPack,
            application.Domains.Select(domain => domain.Host + domain.PathPrefix).ToArray(),
            application.SourceId?.Value);
    }

    private DatabaseSummary ToSummary(DatabaseResource database)
    {
        ProjectId projectId = dbContext.Environments.Where(environment => environment.Id == database.EnvironmentId)
            .Select(environment => environment.ProjectId).Single();

        return new DatabaseSummary(database.Id.Value, database.Name.Value,
            database.Description == null ? null : database.Description.Value.Value, projectId.Value,
            database.EnvironmentId.Value, database.ServerId.Value, database.Engine, database.Version.Value,
            database.HealthState, database.CredentialsReferenceId.Value, DatabaseEngineProfiles.EffectiveImage(database),
            database.LifecycleState, database.HealthCheckEnabled);
    }

    private static ServerSummary ToSummary(Server server)
    {
        return new ServerSummary(server.Id.Value, server.Name.Value,
            server.Description == null ? null : server.Description.Value.Value, server.Address.ToString(),
            server.ConnectionType, server.Runtime, server.Status, server.Labels);
    }

    private static EnvironmentSummary ToSummary(EnvironmentEntity environment)
    {
        return new EnvironmentSummary(environment.Id.Value, environment.ProjectId.Value, environment.Name.Value,
            environment.Kind, environment.Description == null ? null : environment.Description.Value.Value);
    }

    private static Description? ToDescription(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : new Description(value);
    }

    private static ServerAddress ToServerAddress(string host, int port, string? user)
    {
        return new ServerAddress(host, new PortNumber(port), user);
    }

    private static async Task<TEntity> GetRequiredAsync<TEntity, TId>(
        IRepository<TEntity, TId> repository,
        TId id,
        CancellationToken cancellationToken)
        where TEntity : Entity<TId>
        where TId : notnull
    {
        return await repository.GetByIdAsync(id, cancellationToken)
               ?? throw new InvalidOperationException($"{typeof(TEntity).Name} was not found.");
    }
}
