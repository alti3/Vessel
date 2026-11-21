using Vessel.Domain.Secrets;
using Vessel.Application.Authorization;
using Vessel.Application.Backups;
using Vessel.Application.Deployments;
using Vessel.Application.ManagedServices;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Backups;
using Vessel.Domain.Databases;
using Vessel.Domain.Projects;
using Vessel.Domain.Servers;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;
using Vessel.UnitTests.Application.SecurityHardening;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.UnitTests.Application.Backups;

/// <summary>An owner and a plain member in one team, a project with a running PostgreSQL database.</summary>
internal sealed class BackupFixture
{
    public BackupFixture()
    {
        Time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        Db = new BackupTestDbContext();
        Vault = new DictionaryVault(Db);
        Locks = new InMemoryDistributedLockManager(Time);
        Authorization = new VesselAuthorizationService(Db);

        Owner = User.Create(new DisplayName("owner"), new EmailAddress("owner@example.com"), Now);
        Owner.GrantInstanceAdmin(Now);
        Member = User.Create(new DisplayName("member"), new EmailAddress("member@example.com"), Now);
        Team = Team.Create(new DisplayName("team"), Owner.Id, true, Now);
        Team.AddMember(Member.Id, TeamRole.Member, Now);
        Outsider = User.Create(new DisplayName("other"), new EmailAddress("other@example.com"), Now);
        OtherTeam = Team.Create(new DisplayName("other team"), Outsider.Id, true, Now);
        Db.UserItems.AddRange([Owner, Member, Outsider]);
        Db.TeamItems.AddRange([Team, OtherTeam]);

        Project project = Project.Create(Team.Id, new ResourceName("project"), Now);
        EnvironmentEntity environment = EnvironmentEntity.CreateProduction(project.Id, Now);
        Server = Server.Create(Team.Id, new ResourceName("host"),
            new ServerAddress("localhost", new PortNumber(22), null), ServerConnectionType.Local,
            ContainerRuntimeKind.Docker, Now);
        Db.ProjectItems.Add(project);
        Db.EnvironmentItems.Add(environment);
        Db.ServerItems.Add(Server);

        Database = DatabaseResource.Create(environment.Id, Server.Id, new ResourceName("main"),
            DatabaseEngine.PostgreSql, new VersionLabel("18-alpine"),
            new StorageConfiguration("data", "/var/lib/postgresql"), SecretReferenceId.New(), Now,
            DatabaseImage.Parse("postgres:18-alpine"));
        Database.MarkProvisioning(Now);
        Database.MarkRunning("vessel-db-main", "docker-compose.yml", Now);
        Db.DatabaseItems.Add(Database);
    }

    public ManualTimeProvider Time { get; }

    public DateTimeOffset Now => Time.GetUtcNow();

    public BackupTestDbContext Db { get; }

    public DictionaryVault Vault { get; }

    public IDistributedLockManager Locks { get; }

    public VesselAuthorizationService Authorization { get; }

    public RecordingJobs Jobs { get; } = new();

    public RecordingAuditWriter Audit { get; } = new();

    public RecordingNotificationEventPublisher Notifications { get; } = new();

    public FakeDatabaseBackupProvider Provider { get; } = new();

    public FakeBackupStorageProvider Storage { get; } = new();

    public AllowAllOutboundPolicy Policy { get; } = new();

    public User Owner { get; }

    public User Member { get; }

    public User Outsider { get; }

    public Team Team { get; }

    public Team OtherTeam { get; }

    public Server Server { get; }

    public DatabaseResource Database { get; }

    public BackupDestinationConnector Connector => new(Vault, Storage);

    public DatabaseBackupService Backups()
    {
        return new DatabaseBackupService(Db, Authorization, Jobs, Jobs, Locks, Vault, new PassthroughRedactor(),
            Provider, Storage, Connector, new BackupArtifactReader(Db, Storage, Connector), Audit, Notifications,
            Time);
    }

    public BackupDestinationService Destinations()
    {
        return new BackupDestinationService(Db, Authorization, Vault, new PassthroughRedactor(), Policy, Connector,
            Audit, Notifications, Time);
    }

    public async Task<BackupDestination> AddDestinationAsync(string name = "offsite", Team? team = null)
    {
        Team owner = team ?? Team;
        SecretReferenceId secret = (await Vault.StoreAsync(owner.Id, SecretScope.Team, $"dest-{name}",
            BackupDestinationConnector.SerializeCredentials("AKIA", "s3cr3t"), new SecretPolicy(false, false, false),
            new SecretTarget(), TestContext.Current.CancellationToken)).Id;
        var destination = BackupDestination.Create(owner.Id, new ResourceName(name), "https://s3.example.com",
            null, "vessel-backups", true, false, secret, Now);
        destination.MarkUsable(Now);
        Db.DestinationItems.Add(destination);
        return destination;
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }

    public void Advance(TimeSpan by)
    {
        _now = _now.Add(by);
    }
}
