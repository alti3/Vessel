using Vessel.Application.Jobs;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Notifications;
using Vessel.Domain.Secrets;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Application;

public sealed class Phase13NotificationDispatchTests
{
    [Fact]
    public async Task Dispatch_routes_enabled_targets_and_schedules_retry_on_provider_failure()
    {
        var teamId = TeamId.New();
        var now = DateTimeOffset.Parse("2026-06-04T12:00:00Z");
        var dbContext = new TestDbContext();
        var dispatcher = new RecordingDispatcher();
        var provider = new FailingProvider();
        var service = new NotificationDispatchService(dbContext, [provider], new TestSecretVault(),
            dispatcher, new FakeTimeProvider(now));

        var target = NotificationTarget.Create(teamId, new ResourceName("Ops webhook"), NotificationChannel.Webhook,
            SecretReferenceId.New(), now);
        target.Configure(new ResourceName("Ops webhook"), target.CredentialsReferenceId, "{}",
            new NotificationDeliveryPolicy(NotificationSeverity.Info, true, false, false), now);
        var notificationEvent = NotificationEvent.Create(teamId, null, "deployment.failed",
            NotificationSeverity.Critical, NotificationTargetType.Deployment, "deployment-1", "Failed",
            "Deployment failed.", "{}", "/deployments/1", now);

        dbContext.Targets.Add(target);
        dbContext.Events.Add(notificationEvent);

        await service.DispatchAsync(notificationEvent.Id.Value);

        Assert.Equal(1, provider.Calls);
        Assert.Single(dbContext.Attempts);
        Assert.Equal(NotificationDeliveryStatus.RetryScheduled, dbContext.Attempts[0].Status);
        Assert.Single(dispatcher.Scheduled);
        Assert.Equal(NotificationEventStatus.Failed, notificationEvent.Status);
        Assert.DoesNotContain("top-secret", dbContext.Attempts[0].FailureReason);
    }

    private sealed class FailingProvider : INotificationProvider
    {
        public int Calls { get; private set; }
        public NotificationChannel Channel => NotificationChannel.Webhook;

        public Task<NotificationDeliveryResult> SendAsync(
            NotificationEventDispatchMessage notification,
            NotificationTargetDispatchContext target,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(NotificationDeliveryResult.Failed("Webhook notification delivery failed with HTTP 500."));
        }
    }

    private sealed class TestSecretVault : ISecretVault
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
            return Task.FromResult("""{"url":"https://example.test","secret":"top-secret"}""");
        }
    }

    private sealed class RecordingDispatcher : IBackgroundJobDispatcher
    {
        public List<TimeSpan> Scheduled { get; } = [];

        public string Enqueue<TJob>(System.Linq.Expressions.Expression<Func<TJob, Task>> methodCall)
        {
            return "enqueued";
        }

        public string Schedule<TJob>(System.Linq.Expressions.Expression<Func<TJob, Task>> methodCall, TimeSpan delay)
        {
            Scheduled.Add(delay);
            return "scheduled";
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    private sealed class TestDbContext : UnavailableVesselDbContext
    {
        public List<NotificationTarget> Targets { get; } = [];
        public List<NotificationEvent> Events { get; } = [];
        public List<NotificationDeliveryAttempt> Attempts { get; } = [];

        public override IQueryable<NotificationTarget> NotificationTargets => Targets.AsQueryable();
        public override IQueryable<NotificationEvent> NotificationEvents => Events.AsQueryable();
        public override IQueryable<NotificationDeliveryAttempt> NotificationDeliveryAttempts => Attempts.AsQueryable();

        public override IRepository<NotificationDeliveryAttempt, NotificationDeliveryAttemptId> NotificationDeliveryAttemptRepository =>
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
}
