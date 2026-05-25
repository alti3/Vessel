using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
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
using Vessel.Domain.ValueObjects;
using ApplicationId = Vessel.Domain.ApplicationId;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.Application.Resources;

public sealed class ResourceManagementService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    ISecretVault secretVault,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public IReadOnlyList<ProjectSummary> ListProjects(UserId actorUserId, TeamId teamId)
    {
        Require(actorUserId, teamId, VesselPermissions.ProjectsRead);

        return dbContext.Projects
            .Where(project => project.TeamId == teamId)
            .OrderBy(project => project.Name)
            .Select(project => new ProjectSummary(
                project.Id.Value,
                project.Name.Value,
                project.Description == null ? null : project.Description.Value.Value,
                project.IsArchived,
                dbContext.Environments.Count(environment => environment.ProjectId == project.Id)))
            .ToArray();
    }

    public ProjectDetails GetProject(UserId actorUserId, TeamId teamId, ProjectId projectId)
    {
        RequireProject(actorUserId, teamId, projectId, VesselPermissions.ProjectsRead);
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
        RequireProject(actorUserId, teamId, projectId, VesselPermissions.ProjectsWrite);
        Project project = await GetRequiredAsync(dbContext.ProjectRepository, projectId, cancellationToken);
        project.Archive(Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.ProjectArchived, "project", project.Id.Value,
            cancellationToken);
    }

    public IReadOnlyList<EnvironmentSummary> ListEnvironments(UserId actorUserId, TeamId teamId, ProjectId projectId)
    {
        RequireProject(actorUserId, teamId, projectId, VesselPermissions.ProjectsRead);

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
        RequireProject(actorUserId, teamId, environment.ProjectId, VesselPermissions.ProjectsWrite);
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
        Require(actorUserId, teamId, VesselPermissions.ServersRead);

        return dbContext.Servers
            .Where(server => server.TeamId == teamId)
            .OrderBy(server => server.Name)
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
        DateTimeOffset now = Now();
        var server = Server.Create(teamId, new ResourceName(request.Name),
            ToServerAddress(request.Host, request.Port, request.User),
            request.ConnectionType, request.Runtime, now, ToDescription(request.Description));
        server.UpdateSettings(server.Name, server.Description, server.Address, request.ConnectionType, request.Runtime,
            request.Labels, now);
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
        DateTimeOffset now = Now();
        server.ChangeStatus(ServerStatus.Reachable, now);
        var snapshot = ServerStatusSnapshot.Create(server.Id, server.Status, null, null, null, 0, true, true, now);
        await dbContext.ServerStatusSnapshotRepository.AddAsync(snapshot, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.ServerConnectivityChecked, "server", server.Id.Value,
            cancellationToken);

        return new ServerConnectivityResult(server.Id.Value, true, server.Runtime,
            "Server record is valid. Runtime probing is deferred to deployment/runtime phases.", now);
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
        Require(actorUserId, teamId, VesselPermissions.ApplicationsRead);
        return ApplicationsForTeam(teamId).OrderBy(application => application.Name).Select(ToSummary).ToArray();
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
        EnsureEnvironmentBelongsToProject(environmentId, projectId);

        DateTimeOffset now = Now();
        var application = Domain.Applications.Application.Create(environmentId, serverId,
            new ResourceName(request.Name),
            new GitSource(new RepositoryUrl(request.RepositoryUrl), request.Branch),
            BuildConfig(request.BuildPack, request.BaseDirectory, request.DockerfilePath), now);
        application.UpdateSettings(application.Name, ToDescription(request.Description), application.GitSource,
            application.BuildConfiguration, RuntimeConfig(request.ExposedPort), DeploymentSettings.Default, now);
        foreach (var domain in request.Domains.Where(domain => !string.IsNullOrWhiteSpace(domain)))
            application.AddDomain(new DomainName(domain), now);

        await dbContext.ApplicationRepository.AddAsync(application, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.ApplicationCreated, "application", application.Id.Value,
            cancellationToken);

        return ToSummary(application);
    }

    public IReadOnlyList<DatabaseSummary> ListDatabases(UserId actorUserId, TeamId teamId)
    {
        Require(actorUserId, teamId, VesselPermissions.ProjectsRead);
        return DatabasesForTeam(teamId).OrderBy(database => database.Name).Select(ToSummary).ToArray();
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
        EnsureEnvironmentBelongsToProject(environmentId, projectId);
        SecretReference secret = await secretVault.StoreAsync(teamId, SecretScope.Database,
            $"{request.Name}:credentials",
            request.Credentials, SecretPolicy.Default, new SecretTarget(projectId, environmentId, serverId),
            cancellationToken);
        var database = DatabaseResource.Create(environmentId, serverId, new ResourceName(request.Name), request.Engine,
            new VersionLabel(request.Version), new StorageConfiguration(request.VolumeName, request.MountPath),
            secret.Id, Now());
        await dbContext.DatabaseResourceRepository.AddAsync(database, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(teamId, actorUserId, AuditActions.DatabaseCreated, "database", database.Id.Value,
            cancellationToken);

        return ToSummary(database);
    }

    public IReadOnlyList<EnvironmentVariableSummary> ListEnvironmentVariables(UserId actorUserId, TeamId teamId)
    {
        Require(actorUserId, teamId, VesselPermissions.ProjectsRead);
        var canReveal = authorization.HasPermission(actorUserId, teamId, VesselPermissions.SecretsRead);

        return dbContext.EnvironmentVariables
            .Where(variable => variable.TeamId == teamId)
            .OrderBy(variable => variable.TargetType)
            .ThenBy(variable => variable.Key)
            .Select(variable => new EnvironmentVariableSummary(
                variable.Id.Value,
                variable.TargetType,
                variable.Key.Value,
                variable.ValueKind,
                variable.ValueKind == EnvironmentVariableValueKind.Secret
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

    private void RequireProject(UserId actorUserId, TeamId teamId, ProjectId projectId, string permission)
    {
        Require(actorUserId, teamId, permission);
        if (!authorization.CanAccessProject(actorUserId, projectId))
            throw new UnauthorizedAccessException("Project is outside the active team.");
    }

    private void RequireServer(UserId actorUserId, TeamId teamId, ServerId serverId, string permission)
    {
        Require(actorUserId, teamId, permission);
        if (!authorization.CanAccessServer(actorUserId, serverId))
            throw new UnauthorizedAccessException("Server is outside the active team.");
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

        _ = teamId;
        return new SecretTarget(projectId, environmentId, serverId, applicationId, databaseId);
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
            application.Domains.Select(domain => domain.DomainName.Value).ToArray());
    }

    private DatabaseSummary ToSummary(DatabaseResource database)
    {
        ProjectId projectId = dbContext.Environments.Where(environment => environment.Id == database.EnvironmentId)
            .Select(environment => environment.ProjectId).Single();

        return new DatabaseSummary(database.Id.Value, database.Name.Value,
            database.Description == null ? null : database.Description.Value.Value, projectId.Value,
            database.EnvironmentId.Value, database.ServerId.Value, database.Engine, database.Version.Value,
            database.HealthState, database.CredentialsReferenceId.Value);
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
