using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Common;
using Vessel.Application.Email;
using Vessel.Application.Jobs;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Realtime;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Notifications;
using Vessel.Domain.Secrets;
using Vessel.Domain.Settings;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.UnitTests.Application.SecurityHardening;

namespace Vessel.UnitTests.Application.Notifications;

/// <summary>In-memory wiring of the notification and instance email services for unit tests.</summary>
internal sealed class NotificationTestKit
{
    private NotificationTestKit(NotificationTestDb db, IReadOnlyList<INotificationProvider> providers,
        ISecretVault vault, ISecretRedactor redactor, string? publicUrl, DateTimeOffset now)
    {
        Db = db;
        Time = new MutableTimeProvider(now);
        Throttle = new InMemoryNotificationThrottle(Time);
        Links = new InstanceLinkBuilder(Options.Create(new InstanceUrlOptions { PublicUrl = publicUrl }));
        Sender = new InstanceEmailSender(db, Cipher, Smtp, Resend);
        Outbox = new InstanceEmailOutbox(Jobs, Cipher);
        Authorization = new VesselAuthorizationService(db);
        Center = new NotificationCenterService(db, Authorization, new NullRealtime(), Time);
        Events = new NotificationEventService(db, Center, Jobs, redactor, Throttle, Time);
        Dispatch = new NotificationDispatchService(db, providers, vault, redactor, Jobs, Events, Outbox, Sender, Links,
            Audit, Time);
    }

    public NotificationTestDb Db { get; }
    public MutableTimeProvider Time { get; }
    public FakeSecretCipher Cipher { get; } = new("v1");
    public RecordingJobs Jobs { get; } = new();
    public RecordingAudit Audit { get; } = new();
    public RecordingSmtpTransport Smtp { get; } = new();
    public RecordingResendTransport Resend { get; } = new();
    public InMemoryNotificationThrottle Throttle { get; }
    public InstanceLinkBuilder Links { get; }
    public InstanceEmailSender Sender { get; }
    public InstanceEmailOutbox Outbox { get; }
    public VesselAuthorizationService Authorization { get; }
    public NotificationCenterService Center { get; }
    public NotificationEventService Events { get; }
    public NotificationDispatchService Dispatch { get; }

    public static NotificationTestKit Create(
        IReadOnlyList<INotificationProvider>? providers = null,
        NotificationTestDb? db = null,
        ISecretVault? vault = null,
        ISecretRedactor? redactor = null,
        string? publicUrl = "https://vessel.example.com",
        DateTimeOffset? now = null)
    {
        return new NotificationTestKit(db ?? new NotificationTestDb(), providers ?? [], vault ?? new StaticSecretVault(),
            redactor ?? new PassthroughRedactor(), publicUrl, now ?? DateTimeOffset.Parse("2026-11-20T12:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Builds a dispatcher around an existing test database (for tests that own their fakes).</summary>
    public static NotificationDispatchService CreateDispatch(IVesselDbContext db,
        IEnumerable<INotificationProvider> providers, ISecretVault vault, ISecretRedactor redactor,
        IBackgroundJobDispatcher jobs, TimeProvider time)
    {
        var cipher = new FakeSecretCipher("v1");
        var center = new NotificationCenterService(db, new VesselAuthorizationService(db), new NullRealtime(), time);
        var events = new NotificationEventService(db, center, jobs, redactor, new InMemoryNotificationThrottle(time),
            time);
        return new NotificationDispatchService(db, providers, vault, redactor, jobs, events,
            new InstanceEmailOutbox(jobs, cipher),
            new InstanceEmailSender(db, cipher, new RecordingSmtpTransport(), new RecordingResendTransport()),
            new InstanceLinkBuilder(Options.Create(new InstanceUrlOptions())), new RecordingAudit(), time);
    }

    /// <summary>Configures instance SMTP email so channel-disabled and auth emails can be queued.</summary>
    public void ConfigureInstanceSmtp(string? notificationFrom = null)
    {
        var settings = InstanceEmailSettings.Create(Time.GetUtcNow());
        settings.Update(EmailProvider.Smtp, "Default",
            [
                SmtpServerSettings.Create("Default", "smtp.example.com", 465, SmtpEncryption.Tls, "mailer",
                    Cipher.Protect("smtp-password", InstanceEmailSender.SmtpPasswordPurpose), "noreply@example.com",
                    "Vessel", "vessel.example.com", 30)
            ],
            null, null, null, notificationFrom, notificationFrom is null ? null : "Vessel Alerts", Time.GetUtcNow());
        Db.EmailSettings.Add(settings);
    }

    /// <summary>Decrypts the email messages queued through <see cref="InstanceEmailOutbox" />.</summary>
    public IReadOnlyList<EmailMessage> QueuedEmails()
    {
        return Jobs.Enqueued
            .Where(job => job.JobType == typeof(SendInstanceEmailJob))
            .Select(job => System.Text.Json.JsonSerializer.Deserialize<EmailMessage>(
                Cipher.Unprotect((string)job.Arguments[0]!, "vessel:instance-email:queued-message"))!)
            .ToArray();
    }

    public NotificationTarget AddTarget(NotificationChannel channel, string name = "Ops",
        NotificationSeverity minimumSeverity = NotificationSeverity.Info, TeamId? teamId = null)
    {
        var team = teamId ?? Db.TeamId;
        var target = NotificationTarget.Create(team, new Vessel.Domain.ValueObjects.ResourceName(name), channel,
            SecretReferenceId.New(), Time.GetUtcNow());
        target.Configure(target.Name, target.CredentialsReferenceId, "{}",
            new NotificationDeliveryPolicy(minimumSeverity, true, true, true), Time.GetUtcNow());
        Db.Targets.Add(target);
        return target;
    }

    public NotificationEvent AddEvent(string eventType, NotificationSeverity severity = NotificationSeverity.Error,
        string? resourceUrl = "/deployments/1")
    {
        var notificationEvent = NotificationEvent.Create(Db.TeamId, null, eventType, severity,
            NotificationTargetType.Deployment, "deployment-1", "Title", "Message.", "{}", resourceUrl,
            Time.GetUtcNow());
        Db.Events.Add(notificationEvent);
        return notificationEvent;
    }
}

internal sealed class NotificationTestDb : UnavailableVesselDbContext, IVesselDbContext
{
    public NotificationTestDb()
    {
        var owner = User.Create(new Vessel.Domain.ValueObjects.DisplayName("Owner"),
            new Vessel.Domain.ValueObjects.EmailAddress("owner@example.com"), DateTimeOffset.UtcNow);
        var member = User.Create(new Vessel.Domain.ValueObjects.DisplayName("Member"),
            new Vessel.Domain.ValueObjects.EmailAddress("member@example.com"), DateTimeOffset.UtcNow);
        var team = Team.Create(new Vessel.Domain.ValueObjects.DisplayName("Team"), owner.Id, false,
            DateTimeOffset.UtcNow);
        team.AddMember(member.Id, TeamRole.Member, DateTimeOffset.UtcNow);
        Owner = owner;
        Member = member;
        Team = team;
        UsersList.AddRange([owner, member]);
        TeamsList.Add(team);
        Memberships.AddRange(team.Memberships);
    }

    public User Owner { get; }
    public User Member { get; }
    public Team Team { get; }
    public TeamId TeamId => Team.Id;
    public List<User> UsersList { get; } = [];
    public List<Team> TeamsList { get; } = [];
    public List<TeamMembership> Memberships { get; } = [];
    public List<NotificationTarget> Targets { get; } = [];
    public List<NotificationEvent> Events { get; } = [];
    public List<InAppNotification> InApp { get; } = [];
    public List<NotificationDeliveryAttempt> Attempts { get; } = [];
    public List<InstanceEmailSettings> EmailSettings { get; } = [];
    public List<TeamInvitation> Invitations { get; } = [];
    public int SaveCount { get; private set; }
    public Exception? FailNextSave { get; set; }

    IQueryable<User> IVesselDbContext.Users => UsersList.AsQueryable();
    IQueryable<Team> IVesselDbContext.Teams => TeamsList.AsQueryable();
    IQueryable<TeamMembership> IVesselDbContext.TeamMemberships => Memberships.AsQueryable();
    IRepository<User, UserId> IVesselDbContext.UserRepository => new ListRepository<User, UserId>(UsersList);
    IRepository<Team, TeamId> IVesselDbContext.TeamRepository => new ListRepository<Team, TeamId>(TeamsList);
    IQueryable<TeamInvitation> IVesselDbContext.TeamInvitations => Invitations.AsQueryable();

    IRepository<TeamInvitation, TeamInvitationId> IVesselDbContext.TeamInvitationRepository =>
        new ListRepository<TeamInvitation, TeamInvitationId>(Invitations);
    public override IQueryable<NotificationTarget> NotificationTargets => Targets.AsQueryable();
    public override IQueryable<NotificationEvent> NotificationEvents => Events.AsQueryable();
    public override IQueryable<InAppNotification> InAppNotifications => InApp.AsQueryable();
    public override IQueryable<NotificationDeliveryAttempt> NotificationDeliveryAttempts => Attempts.AsQueryable();
    public override IQueryable<InstanceEmailSettings> InstanceEmailSettings => EmailSettings.AsQueryable();

    public override IRepository<NotificationTarget, NotificationTargetId> NotificationTargetRepository =>
        new ListRepository<NotificationTarget, NotificationTargetId>(Targets);

    public override IRepository<NotificationEvent, NotificationEventId> NotificationEventRepository =>
        new ListRepository<NotificationEvent, NotificationEventId>(Events);

    public override IRepository<InAppNotification, InAppNotificationId> InAppNotificationRepository =>
        new ListRepository<InAppNotification, InAppNotificationId>(InApp);

    public override IRepository<NotificationDeliveryAttempt, NotificationDeliveryAttemptId>
        NotificationDeliveryAttemptRepository =>
        new ListRepository<NotificationDeliveryAttempt, NotificationDeliveryAttemptId>(Attempts);

    public override IRepository<InstanceEmailSettings, InstanceEmailSettingsId> InstanceEmailSettingsRepository =>
        new ListRepository<InstanceEmailSettings, InstanceEmailSettingsId>(EmailSettings);

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (FailNextSave is { } failure)
        {
            FailNextSave = null;
            throw failure;
        }

        SaveCount++;
        return Task.FromResult(1);
    }
}

internal sealed class ListRepository<TEntity, TId>(List<TEntity> items) : IRepository<TEntity, TId>
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

internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow()
    {
        return Now;
    }

    public void Advance(TimeSpan by)
    {
        Now = Now.Add(by);
    }
}

/// <summary>Same claim/release semantics as the PostgreSQL implementation, in memory.</summary>
internal sealed class InMemoryNotificationThrottle(TimeProvider timeProvider) : INotificationThrottle
{
    public Dictionary<(TeamId, string, string), DateTimeOffset> Claims { get; } = [];

    public Task<bool> TryClaimAsync(TeamId teamId, string eventType, string subjectKey, TimeSpan? interval,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var key = (teamId, eventType, subjectKey);
        if (Claims.TryGetValue(key, out var sentAt))
        {
            if (interval is null || sentAt >= now - interval.Value)
                return Task.FromResult(false);
        }

        Claims[key] = now;
        return Task.FromResult(true);
    }

    public Task<bool> ReleaseAsync(TeamId teamId, string eventType, string subjectKey,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Claims.Remove((teamId, eventType, subjectKey)));
    }

    public Task<int> PurgeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        var stale = Claims.Where(pair => pair.Value < olderThan).Select(pair => pair.Key).ToArray();
        foreach (var key in stale) Claims.Remove(key);
        return Task.FromResult(stale.Length);
    }
}

internal sealed record RecordedJob(Type JobType, string Method, object?[] Arguments, TimeSpan? Delay);

internal sealed class RecordingJobs : IBackgroundJobDispatcher
{
    public List<RecordedJob> Enqueued { get; } = [];
    public List<RecordedJob> Scheduled { get; } = [];

    public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall)
    {
        Enqueued.Add(Record(methodCall, null));
        return "job";
    }

    public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay)
    {
        Scheduled.Add(Record(methodCall, delay));
        return "job";
    }

    private static RecordedJob Record<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan? delay)
    {
        var call = (MethodCallExpression)methodCall.Body;
        var arguments = call.Arguments
            .Select(argument => Expression.Lambda(Expression.Convert(argument, typeof(object))).Compile()
                .DynamicInvoke())
            .ToArray();
        return new RecordedJob(typeof(TJob), call.Method.Name, arguments, delay);
    }
}

internal sealed record AuditEntry(string Action, AuditTarget Target, IReadOnlyDictionary<string, object?> Metadata);

internal sealed class RecordingAudit : IAuditWriter
{
    public List<AuditEntry> Records { get; } = [];

    public Task RecordAsync(TeamId? teamId, UserId? actorUserId, string action, AuditTarget target,
        string? correlationId, IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken = default)
    {
        Records.Add(new AuditEntry(action, target, metadata));
        return Task.CompletedTask;
    }
}

internal sealed class NullRealtime : IRealtimeNotifier
{
    public Task PublishAsync(RealtimeGroup group, RealtimeMessage message,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}

internal sealed class PassthroughRedactor : ISecretRedactor
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

internal sealed class StaticSecretVault(string secretJson = """{"url":"https://hooks.example.com/x"}""") : ISecretVault
{
    public Task<SecretReference> StoreAsync(TeamId teamId, SecretScope scope, string key, string plaintext,
        SecretPolicy policy, SecretTarget target, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task ReplaceAsync(SecretReferenceId secretReferenceId, string plaintext,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<string> RevealAsync(UserId actorUserId, TeamId teamId, SecretReferenceId secretReferenceId,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<string> RevealForDeploymentAsync(TeamId teamId, SecretReferenceId secretReferenceId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(secretJson);
    }
}

internal sealed record SentEmail(EmailSender From, EmailMessage Message, SmtpTransportSettings? Smtp, string? ApiKey);

internal sealed class RecordingSmtpTransport : ISmtpEmailTransport
{
    public List<SentEmail> Sent { get; } = [];
    public EmailSendResult Result { get; set; } = EmailSendResult.Sent("smtp-1");

    public Task<EmailSendResult> SendAsync(SmtpTransportSettings settings, EmailSender from, EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        Sent.Add(new SentEmail(from, message, settings, null));
        return Task.FromResult(Result);
    }
}

internal sealed class RecordingResendTransport : IResendEmailTransport
{
    public List<SentEmail> Sent { get; } = [];

    public Task<EmailSendResult> SendAsync(string apiKey, EmailSender from, EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        Sent.Add(new SentEmail(from, message, null, apiKey));
        return Task.FromResult(EmailSendResult.Sent("resend-1"));
    }
}

/// <summary>Provider whose next results are scripted; records the dispatch contexts it saw.</summary>
internal sealed class ScriptedProvider(NotificationChannel channel) : INotificationProvider
{
    public Queue<NotificationDeliveryResult> Results { get; } = new();
    public NotificationDeliveryResult Default { get; set; } = NotificationDeliveryResult.Succeeded();
    public List<(NotificationEventDispatchMessage Message, NotificationTargetDispatchContext Target)> Calls { get; } = [];
    public NotificationChannel Channel => channel;

    public Task<NotificationDeliveryResult> SendAsync(NotificationEventDispatchMessage notification,
        NotificationTargetDispatchContext target, CancellationToken cancellationToken = default)
    {
        Calls.Add((notification, target));
        return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Default);
    }
}
