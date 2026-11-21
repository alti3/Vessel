using System.Linq.Expressions;
using System.Net;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Jobs;
using Vessel.Application.Notifications;
using Vessel.Application.OutboundRequests;
using Vessel.Application.Persistence;
using Vessel.Application.Realtime;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Notifications;
using Vessel.Domain.Secrets;
using Vessel.Domain.Teams;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Application;

public sealed class NotificationSecurityTests
{
    private const string SlackSecret = """{"webhookUrl":"https://hooks.slack.com/services/T000/B000/top-secret-path"}""";

    [Fact]
    public void MembersCanReadButNotWriteNotifications()
    {
        IReadOnlySet<string> member = VesselPermissions.ForRole(TeamRole.Member);
        Assert.Contains(VesselPermissions.NotificationsRead, member);
        Assert.DoesNotContain(VesselPermissions.NotificationsWrite, member);
        Assert.Contains(VesselPermissions.NotificationsWrite, VesselPermissions.ForRole(TeamRole.Admin));
        Assert.Contains(VesselPermissions.NotificationsWrite, VesselPermissions.ForRole(TeamRole.Owner));
    }

    [Fact]
    public void ReadTokenScopeDoesNotGrantNotificationWrite()
    {
        IReadOnlySet<string> read = TokenScopeMapper.ToPermissions([TokenScopeMapper.Read]);
        Assert.Contains(VesselPermissions.NotificationsRead, read);
        Assert.DoesNotContain(VesselPermissions.NotificationsWrite, read);
        Assert.Contains(VesselPermissions.NotificationsWrite, TokenScopeMapper.ToPermissions([TokenScopeMapper.Write]));
    }

    [Fact]
    public async Task MemberCannotCreateOrReplaceNotificationTargets()
    {
        var fixture = Fixture.Create();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Targets.UpsertTargetAsync(
            fixture.TeamId, fixture.MemberId, SlackRequest(SlackSecret), TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Db.Targets);
        Assert.Empty(fixture.Vault.Stored);
    }

    [Fact]
    public async Task MemberCannotSendTestNotification()
    {
        var fixture = Fixture.Create();
        await fixture.Targets.UpsertTargetAsync(fixture.TeamId, fixture.OwnerId, SlackRequest(SlackSecret),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Targets.SendTestAsync(fixture.TeamId,
            fixture.MemberId, fixture.Db.Targets[0].Id, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Db.Events);
    }

    [Fact]
    public void NonMemberCannotListTargetsOrInAppNotifications()
    {
        var fixture = Fixture.Create();
        var outsider = UserId.New();

        Assert.Throws<UnauthorizedAccessException>(() => fixture.Targets.ListTargets(fixture.TeamId, outsider));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Center.ListInApp(fixture.TeamId, outsider));
    }

    [Fact]
    public async Task OwnerUpsertStoresSecretInVaultAndNeverReturnsIt()
    {
        var fixture = Fixture.Create();

        NotificationTargetListItem result = await fixture.Targets.UpsertTargetAsync(fixture.TeamId, fixture.OwnerId,
            SlackRequest(SlackSecret), TestContext.Current.CancellationToken);

        Assert.True(result.HasCredentials);
        Assert.Contains("top-secret-path", Assert.Single(fixture.Vault.Stored), StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", fixture.Db.Targets[0].ConfigurationJson, StringComparison.Ordinal);
        NotificationTargetListItem listed = Assert.Single(fixture.Targets.ListTargets(fixture.TeamId, fixture.MemberId));
        Assert.DoesNotContain("top-secret", listed.ToString(), StringComparison.Ordinal);
        AuditRecord audit = Assert.Single(fixture.Audit.Records);
        Assert.DoesNotContain(audit.Metadata.Values, value => value?.ToString()?.Contains("top-secret",
            StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task UpsertRejectsUrlsRefusedByOutboundPolicy()
    {
        var fixture = Fixture.Create();
        fixture.Policy.Deny = "the host resolves to a link-local or cloud metadata address.";

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() =>
            fixture.Targets.UpsertTargetAsync(fixture.TeamId, fixture.OwnerId,
                new UpsertNotificationTargetRequest("hook", NotificationChannel.Webhook, true, NotificationSeverity.Info,
                    true, true, true, null, """{"url":"https://169.254.169.254/latest/meta-data/?token=top-secret"}"""),
                TestContext.Current.CancellationToken));

        Assert.Contains("link-local", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", exception.Message, StringComparison.Ordinal);
        Assert.Equal("https://169.254.169.254/latest/meta-data/?token=top-secret", fixture.Policy.CheckedUrls.Single());
        Assert.Empty(fixture.Vault.Stored);
        Assert.Empty(fixture.Db.Targets);
    }

    [Fact]
    public async Task UpsertRejectsTelegramTokensThatCouldInjectPaths()
    {
        var fixture = Fixture.Create();

        await Assert.ThrowsAsync<DomainException>(() => fixture.Targets.UpsertTargetAsync(fixture.TeamId,
            fixture.OwnerId,
            new UpsertNotificationTargetRequest("tg", NotificationChannel.Telegram, true, NotificationSeverity.Info,
                true, true, true, null, """{"botToken":"123:abc/../../evil","chatId":"1"}"""),
            TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Vault.Stored);
    }

    [Fact]
    public async Task InvalidSecretJsonIsNotEchoedInErrors()
    {
        var fixture = Fixture.Create();

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => fixture.Targets.UpsertTargetAsync(
            fixture.TeamId, fixture.OwnerId, SlackRequest("{ top-secret"), TestContext.Current.CancellationToken));

        Assert.DoesNotContain("top-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendTestRoutesOnlyToTargetUnderTest()
    {
        var fixture = Fixture.Create();
        await fixture.Targets.UpsertTargetAsync(fixture.TeamId, fixture.OwnerId, SlackRequest(SlackSecret),
            TestContext.Current.CancellationToken);
        await fixture.Targets.UpsertTargetAsync(fixture.TeamId, fixture.OwnerId,
            SlackRequest(SlackSecret) with { Name = "Other" }, TestContext.Current.CancellationToken);
        NotificationTarget underTest = fixture.Db.Targets[0];

        await fixture.Targets.SendTestAsync(fixture.TeamId, fixture.OwnerId, underTest.Id,
            TestContext.Current.CancellationToken);

        NotificationEvent testEvent = Assert.Single(fixture.Db.Events);
        Assert.Equal(NotificationEventTypes.Test, testEvent.EventType);
        Assert.Empty(fixture.Db.InApp); // tests never fan out to the in-app center.
        Assert.Contains(fixture.Audit.Records, record => record.Action == AuditActions.NotificationTargetTested);

        var provider = new RecordingProvider();
        var dispatch = Notifications.NotificationTestKit.CreateDispatch(fixture.Db, [provider], fixture.Vault,
            new PassthroughRedactor(), fixture.Jobs, TimeProvider.System);
        await dispatch.DispatchAsync(testEvent.Id.Value, TestContext.Current.CancellationToken);

        Assert.Equal([underTest.Id.Value], provider.TargetIds);
    }

    [Fact]
    public async Task SendTestCannotReachAnotherTeamsTarget()
    {
        var fixture = Fixture.Create();
        var foreignTarget = NotificationTarget.Create(TeamId.New(), new ResourceName("foreign"),
            NotificationChannel.Slack, SecretReferenceId.New(), DateTimeOffset.UtcNow);
        fixture.Db.Targets.Add(foreignTarget);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Targets.SendTestAsync(fixture.TeamId,
            fixture.OwnerId, foreignTarget.Id, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Db.Events);
    }

    [Fact]
    public async Task RaiseNotificationJobPersistsEventAndQueuesDispatch()
    {
        var fixture = Fixture.Create();
        var job = new RaiseNotificationJob(fixture.Events);

        await job.RunAsync(fixture.TeamId.Value,
            OperationalNotifications.BackupFailed(Guid.NewGuid(), Guid.NewGuid(), "orders-db"),
            TestContext.Current.CancellationToken);

        NotificationEvent raised = Assert.Single(fixture.Db.Events);
        Assert.Equal(NotificationEventTypes.BackupFailed, raised.EventType);
        Assert.Single(fixture.Db.InApp);
        Assert.Contains(typeof(DispatchNotificationJob), fixture.Jobs.EnqueuedJobTypes);
    }

    private static UpsertNotificationTargetRequest SlackRequest(string secretJson)
    {
        return new UpsertNotificationTargetRequest("Ops", NotificationChannel.Slack, true, NotificationSeverity.Info,
            true, true, true, "{}", secretJson);
    }

    private sealed class Fixture
    {
        public TestDb Db { get; } = new();
        public TestVault Vault { get; } = new();
        public TestPolicy Policy { get; } = new();
        public TestAudit Audit { get; } = new();
        public TestJobs Jobs { get; } = new();
        public TeamId TeamId { get; private init; }
        public UserId OwnerId { get; private init; }
        public UserId MemberId { get; private init; }
        public NotificationTargetService Targets { get; private set; } = null!;
        public NotificationCenterService Center { get; private set; } = null!;
        public NotificationEventService Events { get; private set; } = null!;

        public static Fixture Create()
        {
            var owner = UserId.New();
            var member = UserId.New();
            var team = Team.Create(new DisplayName("Team"), owner, false, DateTimeOffset.UtcNow);
            team.AddMember(member, TeamRole.Member, DateTimeOffset.UtcNow);
            var fixture = new Fixture { TeamId = team.Id, OwnerId = owner, MemberId = member };
            fixture.Db.Memberships.AddRange(team.Memberships);

            var authorization = new VesselAuthorizationService(fixture.Db);
            fixture.Center = new NotificationCenterService(fixture.Db, authorization, new NullRealtime(),
                TimeProvider.System);
            fixture.Events = new NotificationEventService(fixture.Db, fixture.Center, fixture.Jobs,
                new PassthroughRedactor(), new Notifications.InMemoryNotificationThrottle(TimeProvider.System),
                TimeProvider.System);
            fixture.Targets = new NotificationTargetService(fixture.Db, authorization, fixture.Vault, fixture.Policy,
                fixture.Events, fixture.Audit, TimeProvider.System);
            return fixture;
        }
    }

    private sealed class TestDb : UnavailableVesselDbContext, IVesselDbContext
    {
        public List<TeamMembership> Memberships { get; } = [];
        public List<NotificationTarget> Targets { get; } = [];
        public List<NotificationEvent> Events { get; } = [];
        public List<InAppNotification> InApp { get; } = [];
        public List<NotificationDeliveryAttempt> Attempts { get; } = [];

        IQueryable<TeamMembership> IVesselDbContext.TeamMemberships => Memberships.AsQueryable();
        public override IQueryable<NotificationTarget> NotificationTargets => Targets.AsQueryable();
        public override IQueryable<NotificationEvent> NotificationEvents => Events.AsQueryable();
        public override IQueryable<InAppNotification> InAppNotifications => InApp.AsQueryable();
        public override IQueryable<NotificationDeliveryAttempt> NotificationDeliveryAttempts => Attempts.AsQueryable();

        public override IRepository<NotificationTarget, NotificationTargetId> NotificationTargetRepository =>
            new ListRepository<NotificationTarget, NotificationTargetId>(Targets);

        public override IRepository<NotificationEvent, NotificationEventId> NotificationEventRepository =>
            new ListRepository<NotificationEvent, NotificationEventId>(Events);

        public override IRepository<InAppNotification, InAppNotificationId> InAppNotificationRepository =>
            new ListRepository<InAppNotification, InAppNotificationId>(InApp);

        public override IRepository<NotificationDeliveryAttempt, NotificationDeliveryAttemptId>
            NotificationDeliveryAttemptRepository =>
            new ListRepository<NotificationDeliveryAttempt, NotificationDeliveryAttemptId>(Attempts);

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1);
        }
    }

    private sealed class ListRepository<TEntity, TId>(List<TEntity> items) : IRepository<TEntity, TId>
        where TEntity : Entity<TId>
        where TId : notnull
    {
        public Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(items.FirstOrDefault(item => EqualityComparer<TId>.Default.Equals(item.Id, id)));
        }

        public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            items.Add(entity);
            return Task.CompletedTask;
        }

        public void Remove(TEntity entity)
        {
            items.Remove(entity);
        }
    }

    private sealed class TestVault : ISecretVault
    {
        public List<string> Stored { get; } = [];

        public Task<SecretReference> StoreAsync(TeamId teamId, SecretScope scope, string key, string plaintext,
            SecretPolicy policy, SecretTarget target, CancellationToken cancellationToken = default)
        {
            Stored.Add(plaintext);
            return Task.FromResult(SecretReference.Create(teamId, scope, key, SecretProvider.Vessel, "ref", policy,
                DateTimeOffset.UtcNow));
        }

        public Task ReplaceAsync(SecretReferenceId secretReferenceId, string plaintext,
            CancellationToken cancellationToken = default)
        {
            Stored.Add(plaintext);
            return Task.CompletedTask;
        }

        public Task<string> RevealAsync(UserId actorUserId, TeamId teamId, SecretReferenceId secretReferenceId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> RevealForDeploymentAsync(TeamId teamId, SecretReferenceId secretReferenceId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SlackSecret);
        }
    }

    private sealed class TestPolicy : IOutboundUrlPolicy
    {
        public string? Deny { get; set; }
        public List<string> CheckedUrls { get; } = [];

        public Task<OutboundTargetCheck> CheckUrlAsync(Uri url, CancellationToken cancellationToken = default)
        {
            CheckedUrls.Add(url.OriginalString);
            return Task.FromResult(Deny is null ? OutboundTargetCheck.Allow() : OutboundTargetCheck.Deny(Deny));
        }

        public Task<OutboundTargetCheck> CheckHostAsync(string host, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Deny is null ? OutboundTargetCheck.Allow() : OutboundTargetCheck.Deny(Deny));
        }

        public bool IsAddressAllowed(string host, IPAddress address)
        {
            return Deny is null;
        }
    }

    private sealed record AuditRecord(string Action, IReadOnlyDictionary<string, object?> Metadata);

    private sealed class TestAudit : IAuditWriter
    {
        public List<AuditRecord> Records { get; } = [];

        public Task RecordAsync(TeamId? teamId, UserId? actorUserId, string action, AuditTarget target,
            string? correlationId, IReadOnlyDictionary<string, object?> metadata,
            CancellationToken cancellationToken = default)
        {
            Records.Add(new AuditRecord(action, metadata));
            return Task.CompletedTask;
        }
    }

    private sealed class TestJobs : IBackgroundJobDispatcher
    {
        public List<Type> EnqueuedJobTypes { get; } = [];

        public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall)
        {
            EnqueuedJobTypes.Add(typeof(TJob));
            return "job";
        }

        public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay)
        {
            EnqueuedJobTypes.Add(typeof(TJob));
            return "job";
        }
    }

    private sealed class NullRealtime : IRealtimeNotifier
    {
        public Task PublishAsync(RealtimeGroup group, RealtimeMessage message,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class PassthroughRedactor : ISecretRedactor
    {
        public string Redact(string value, RedactionContext? context = null)
        {
            return value;
        }

        public byte[] RedactUtf8(byte[] value, RedactionContext? context = null)
        {
            return value;
        }
    }

    private sealed class RecordingProvider : INotificationProvider
    {
        public List<Guid> TargetIds { get; } = [];
        public NotificationChannel Channel => NotificationChannel.Slack;

        public Task<NotificationDeliveryResult> SendAsync(NotificationEventDispatchMessage notification,
            NotificationTargetDispatchContext target, CancellationToken cancellationToken = default)
        {
            TargetIds.Add(target.TargetId);
            return Task.FromResult(NotificationDeliveryResult.Succeeded());
        }
    }
}
