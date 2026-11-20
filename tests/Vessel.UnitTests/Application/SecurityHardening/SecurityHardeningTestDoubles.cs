using System.Linq.Expressions;
using System.Text;
using Vessel.Application.Auditing;
using Vessel.Application.Docker;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Processes;
using Vessel.Application.Realtime;
using Vessel.Application.Security;
using Vessel.Application.Ssh;
using Vessel.Application.Terminals;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Backups;
using Vessel.Domain.Certificates;
using Vessel.Domain.Common;
using Vessel.Domain.Databases;
using Vessel.Domain.Deployments;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.Notifications;
using Vessel.Domain.Projects;
using Vessel.Domain.Proxy;
using Vessel.Domain.Registries;
using Vessel.Domain.Secrets;
using Vessel.Domain.Servers;
using Vessel.Domain.Services;
using Vessel.Domain.Settings;
using Vessel.Domain.Teams;
using Vessel.Domain.Terminals;
using Vessel.Domain.Users;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.UnitTests.Application.SecurityHardening;

internal sealed class SecurityTestDbContext : IVesselDbContext
{
    public List<User> UserItems { get; } = [];
    public List<Team> TeamItems { get; } = [];
    public List<Project> ProjectItems { get; } = [];
    public List<EnvironmentEntity> EnvironmentItems { get; } = [];
    public List<Server> ServerItems { get; } = [];
    public List<AppEntity> ApplicationItems { get; } = [];
    public List<Deployment> DeploymentItems { get; } = [];
    public List<TerminalSession> TerminalSessionItems { get; } = [];
    public List<ServerStatusSnapshot> ServerStatusSnapshotItems { get; } = [];
    public List<SecretValue> SecretValueItems { get; } = [];
    public int SaveCount { get; private set; }

    public IQueryable<User> Users => UserItems.AsQueryable();
    public IQueryable<Team> Teams => TeamItems.AsQueryable();
    public IQueryable<TeamMembership> TeamMemberships => TeamItems.SelectMany(team => team.Memberships).AsQueryable();
    public IQueryable<TeamInvitation> TeamInvitations => Empty<TeamInvitation>();
    public IQueryable<Project> Projects => ProjectItems.AsQueryable();
    public IQueryable<EnvironmentEntity> Environments => EnvironmentItems.AsQueryable();
    public IQueryable<Server> Servers => ServerItems.AsQueryable();
    public IQueryable<AppEntity> Applications => ApplicationItems.AsQueryable();
    public IQueryable<ApplicationDomain> ApplicationDomains => ApplicationItems.SelectMany(application => application.Domains).AsQueryable();
    public IQueryable<DatabaseResource> DatabaseResources => Empty<DatabaseResource>();
    public IQueryable<ServiceResource> ServiceResources => Empty<ServiceResource>();
    public IQueryable<BackupSchedule> BackupSchedules => Empty<BackupSchedule>();
    public IQueryable<BackupExecution> BackupExecutions => Empty<BackupExecution>();
    public IQueryable<Deployment> Deployments => DeploymentItems.AsQueryable();
    public IQueryable<SecretReference> SecretReferences => Empty<SecretReference>();
    public IQueryable<SecretValue> SecretValues => SecretValueItems.AsQueryable();
    public IQueryable<EnvironmentVariable> EnvironmentVariables => Empty<EnvironmentVariable>();
    public IQueryable<RegistryCredential> RegistryCredentials => Empty<RegistryCredential>();
    public IQueryable<ServerStatusSnapshot> ServerStatusSnapshots => ServerStatusSnapshotItems.AsQueryable();
    public IQueryable<NotificationTarget> NotificationTargets => Empty<NotificationTarget>();
    public IQueryable<NotificationEvent> NotificationEvents => Empty<NotificationEvent>();
    public IQueryable<InAppNotification> InAppNotifications => Empty<InAppNotification>();
    public IQueryable<NotificationDeliveryAttempt> NotificationDeliveryAttempts => Empty<NotificationDeliveryAttempt>();
    public IQueryable<AuditLog> AuditLogs => Empty<AuditLog>();
    public IQueryable<SettingEntry> Settings => Empty<SettingEntry>();
    public IQueryable<PersonalAccessToken> PersonalAccessTokens => Empty<PersonalAccessToken>();
    public IQueryable<WebhookEvent> WebhookEvents => Empty<WebhookEvent>();
    public IQueryable<ApplicationWebhookConfiguration> ApplicationWebhookConfigurations => Empty<ApplicationWebhookConfiguration>();
    public IQueryable<ApplicationPreview> ApplicationPreviews => Empty<ApplicationPreview>();
    public IQueryable<ProxyConfigurationVersion> ProxyConfigurationVersions => Empty<ProxyConfigurationVersion>();
    public IQueryable<Certificate> Certificates => Empty<Certificate>();
    public IQueryable<TerminalSession> TerminalSessions => TerminalSessionItems.AsQueryable();
    public IQueryable<ServerProxySettings> ServerProxySettings => Empty<ServerProxySettings>();

    public IRepository<User, UserId> UserRepository => new ListRepository<User, UserId>(UserItems);
    public IRepository<Team, TeamId> TeamRepository => new ListRepository<Team, TeamId>(TeamItems);
    public IRepository<TeamInvitation, TeamInvitationId> TeamInvitationRepository => EmptyRepo<TeamInvitation, TeamInvitationId>();
    public IRepository<PersonalAccessToken, PersonalAccessTokenId> PersonalAccessTokenRepository => EmptyRepo<PersonalAccessToken, PersonalAccessTokenId>();
    public IRepository<Project, ProjectId> ProjectRepository => new ListRepository<Project, ProjectId>(ProjectItems);
    public IRepository<EnvironmentEntity, EnvironmentId> EnvironmentRepository => new ListRepository<EnvironmentEntity, EnvironmentId>(EnvironmentItems);
    public IRepository<Server, ServerId> ServerRepository => new ListRepository<Server, ServerId>(ServerItems);
    public IRepository<AppEntity, Vessel.Domain.ApplicationId> ApplicationRepository => new ListRepository<AppEntity, Vessel.Domain.ApplicationId>(ApplicationItems);
    public IRepository<DatabaseResource, DatabaseResourceId> DatabaseResourceRepository => EmptyRepo<DatabaseResource, DatabaseResourceId>();
    public IRepository<ServiceResource, ServiceResourceId> ServiceResourceRepository => EmptyRepo<ServiceResource, ServiceResourceId>();
    public IRepository<BackupSchedule, BackupScheduleId> BackupScheduleRepository => EmptyRepo<BackupSchedule, BackupScheduleId>();
    public IRepository<BackupExecution, BackupExecutionId> BackupExecutionRepository => EmptyRepo<BackupExecution, BackupExecutionId>();
    public IRepository<Deployment, DeploymentId> DeploymentRepository => new ListRepository<Deployment, DeploymentId>(DeploymentItems);
    public IRepository<SecretReference, SecretReferenceId> SecretReferenceRepository => EmptyRepo<SecretReference, SecretReferenceId>();
    public IRepository<SecretValue, SecretValueId> SecretValueRepository => new ListRepository<SecretValue, SecretValueId>(SecretValueItems);
    public IRepository<EnvironmentVariable, EnvironmentVariableId> EnvironmentVariableRepository => EmptyRepo<EnvironmentVariable, EnvironmentVariableId>();
    public IRepository<RegistryCredential, RegistryCredentialId> RegistryCredentialRepository => EmptyRepo<RegistryCredential, RegistryCredentialId>();
    public IRepository<ServerStatusSnapshot, ServerStatusSnapshotId> ServerStatusSnapshotRepository => new ListRepository<ServerStatusSnapshot, ServerStatusSnapshotId>(ServerStatusSnapshotItems);
    public IRepository<WebhookEvent, WebhookEventId> WebhookEventRepository => EmptyRepo<WebhookEvent, WebhookEventId>();
    public IRepository<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId> ApplicationWebhookConfigurationRepository => EmptyRepo<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId>();
    public IRepository<ApplicationPreview, ApplicationPreviewId> ApplicationPreviewRepository => EmptyRepo<ApplicationPreview, ApplicationPreviewId>();
    public IRepository<ProxyConfigurationVersion, ProxyConfigurationVersionId> ProxyConfigurationVersionRepository => EmptyRepo<ProxyConfigurationVersion, ProxyConfigurationVersionId>();
    public IRepository<Certificate, CertificateId> CertificateRepository => EmptyRepo<Certificate, CertificateId>();
    public IRepository<TerminalSession, TerminalSessionId> TerminalSessionRepository => new ListRepository<TerminalSession, TerminalSessionId>(TerminalSessionItems);
    public IRepository<ServerProxySettings, ServerId> ServerProxySettingsRepository => EmptyRepo<ServerProxySettings, ServerId>();

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        return Task.FromResult(1);
    }

    private static IQueryable<T> Empty<T>() => Array.Empty<T>().AsQueryable();

    private static IRepository<TEntity, TId> EmptyRepo<TEntity, TId>()
        where TEntity : Entity<TId>
        where TId : notnull => new EmptyRepository<TEntity, TId>();

    private sealed class ListRepository<TEntity, TId>(List<TEntity> items) : IRepository<TEntity, TId>
        where TEntity : Entity<TId>
        where TId : notnull
    {
        public Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken = default) =>
            Task.FromResult(items.SingleOrDefault(item => EqualityComparer<TId>.Default.Equals(item.Id, id)));

        public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            items.Add(entity);
            return Task.CompletedTask;
        }

        public void Remove(TEntity entity) => items.Remove(entity);
    }

    private sealed class EmptyRepository<TEntity, TId> : IRepository<TEntity, TId>
        where TEntity : Entity<TId>
        where TId : notnull
    {
        public Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken = default) => Task.FromResult<TEntity?>(null);
        public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Remove(TEntity entity) => throw new NotSupportedException();
    }
}

internal sealed class RecordingAuditWriter : IAuditWriter
{
    public List<(string Action, IReadOnlyDictionary<string, object?> Metadata)> Records { get; } = [];

    public Task RecordAsync(TeamId? teamId, UserId? actorUserId, string action, AuditTarget target,
        string? correlationId, IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken = default)
    {
        Records.Add((action, metadata));
        return Task.CompletedTask;
    }
}

internal sealed class NullRealtimeNotifier : IRealtimeNotifier
{
    public Task PublishAsync(RealtimeGroup group, RealtimeMessage message, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class PassthroughRedactor : ISecretRedactor
{
    public string Redact(string value, RedactionContext? context = null) => value;
    public byte[] RedactUtf8(byte[] value, RedactionContext? context = null) => value;
}

internal sealed class RecordingTerminalBridge : ITerminalProcessBridge
{
    public List<TerminalBridgeOpenRequest> Opened { get; } = [];

    public Task OpenAsync(TerminalBridgeOpenRequest request, Func<TerminalOutputChunk, CancellationToken, Task> onOutput,
        Func<TerminalSessionId, string?, CancellationToken, Task> onExit, CancellationToken cancellationToken = default)
    {
        Opened.Add(request);
        return Task.CompletedTask;
    }

    public Task SendInputAsync(TerminalSessionId sessionId, string data, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ResizeAsync(TerminalBridgeResizeRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CloseAsync(TerminalSessionId sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Counts local runtime calls so tests can prove remote servers are never answered by the host runtime.</summary>
internal sealed class CountingContainerRuntime : IContainerRuntimeClient
{
    public int Calls { get; private set; }
    public bool Fail { get; init; }

    public Task<ContainerRuntimeInfo> GetInfoAsync(ContainerRuntimeTarget target, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Fail) throw new InvalidOperationException("runtime down");
        return Task.FromResult(new ContainerRuntimeInfo(ContainerRuntimeProvider.Docker, "27.0", "1.46", "linux", "x64"));
    }

    public Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(ContainerRuntimeTarget target, bool all, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<ContainerSummary>>(
            [new ContainerSummary("1", ["app"], "image", "running", "Up", new Dictionary<string, string>())]);
    }

    public Task<string> InspectContainerAsync(ContainerRuntimeTarget target, string containerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<ImageSummary>> ListImagesAsync(ContainerRuntimeTarget target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(ContainerRuntimeTarget target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(ContainerRuntimeTarget target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task EnsureNetworkAsync(ContainerRuntimeTarget target, string name, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<ProcessOutputLine> BuildImageAsync(ContainerRuntimeTarget target, DockerBuildCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<ContainerEvent> StreamEventsAsync(ContainerRuntimeTarget target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<ProcessOutputLine> RunComposeAsync(ContainerRuntimeTarget target, ComposeCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class RecordingSshClient(bool reachable) : ISshClient
{
    public List<SshConnectionOptions> Probes { get; } = [];

    public Task<bool> TestConnectionAsync(SshConnectionOptions options, CancellationToken cancellationToken = default)
    {
        Probes.Add(options);
        return Task.FromResult(reachable);
    }

    public Task<SshCommandResult> RunCommandAsync(SshCommandRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SshCommandResult> RunCommandWithInputAsync(SshCommandRequest request, Stream standardInput, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UploadAsync(SshFileTransferRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DownloadAsync(SshFileTransferRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class FixedProxyStatusProbe(bool healthy) : Vessel.Application.Proxy.IProxyStatusProbe
{
    public Task<bool> IsHealthyAsync(Server server, CancellationToken cancellationToken = default) =>
        Task.FromResult(healthy);
}

internal sealed class RecordingJobDispatcher : IBackgroundJobDispatcher
{
    public int Enqueued { get; private set; }

    public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall)
    {
        Enqueued++;
        return "job-" + Enqueued;
    }

    public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay) => Enqueue(methodCall);
}

/// <summary>
/// Deterministic, reversible stand-in for the AES key ring. Ciphertexts carry their key version, and only
/// <see cref="KnownVersions"/> can be decrypted, which is enough to exercise rotation logic.
/// </summary>
internal sealed class FakeSecretCipher(string activeVersion, params string[] previousVersions) : ISecretCipher
{
    private const string Prefix = "vsec1:";

    public HashSet<string> KnownVersions { get; } = [activeVersion, .. previousVersions];

    public string ActiveKeyVersion => activeVersion;

    public SecretCipherText Encrypt(string plaintext, string associatedData) =>
        new(Encode($"{associatedData}|{plaintext}"), "nonce", "tag", activeVersion);

    public string Decrypt(SecretCipherText cipherText, string associatedData)
    {
        if (!KnownVersions.Contains(cipherText.KeyVersion)) throw new SecretDecryptionException("unknown key");
        string decoded = Decode(cipherText.CipherText);
        string expectedPrefix = associatedData + "|";
        if (!decoded.StartsWith(expectedPrefix, StringComparison.Ordinal)) throw new SecretDecryptionException("aad");
        return decoded[expectedPrefix.Length..];
    }

    public string Protect(string plaintext, string purpose)
    {
        SecretCipherText encrypted = Encrypt(plaintext, purpose);
        return $"{Prefix}{encrypted.KeyVersion}:{encrypted.Nonce}:{encrypted.Tag}:{encrypted.CipherText}";
    }

    public string Unprotect(string protectedValue, string purpose)
    {
        string[] parts = protectedValue.Split(':');
        if (parts.Length != 5) throw new SecretDecryptionException("malformed");
        return Decrypt(new SecretCipherText(parts[4], parts[2], parts[3], parts[1]), purpose);
    }

    public bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal) && value.Split(':').Length == 5;

    public string? GetProtectedKeyVersion(string value) => IsProtected(value) ? value.Split(':')[1] : null;

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
