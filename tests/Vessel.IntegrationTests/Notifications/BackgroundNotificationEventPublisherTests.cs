using System.Linq.Expressions;
using Microsoft.Extensions.Logging.Abstractions;
using Vessel.Application.Jobs;
using Vessel.Application.Notifications;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Notifications;
using Vessel.Infrastructure.Notifications;

namespace Vessel.IntegrationTests.Notifications;

public sealed class BackgroundNotificationEventPublisherTests
{
    [Fact]
    public async Task PublishEnqueuesRaiseJobWithRedactedContent()
    {
        var jobs = new CapturingJobs();
        var publisher = new BackgroundNotificationEventPublisher(jobs, new MaskingRedactor(),
            NullLogger<BackgroundNotificationEventPublisher>.Instance);
        var teamId = TeamId.New();

        await publisher.PublishAsync(teamId, new RaiseNotificationRequest("deployment.failed",
            NotificationSeverity.Error, NotificationTargetType.Deployment, "d1", "Failed s3cr3t", "Message s3cr3t",
            """{"token":"s3cr3t"}""", "/deployments/d1", null), TestContext.Current.CancellationToken);

        (Guid enqueuedTeam, RaiseNotificationRequest enqueued) = Assert.Single(jobs.Raised);
        Assert.Equal(teamId.Value, enqueuedTeam);
        Assert.DoesNotContain("s3cr3t", enqueued.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", enqueued.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", enqueued.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishNeverThrowsIntoProducer()
    {
        var publisher = new BackgroundNotificationEventPublisher(new ThrowingJobs(), new MaskingRedactor(),
            NullLogger<BackgroundNotificationEventPublisher>.Instance);

        await publisher.PublishAsync(TeamId.New(),
            OperationalNotifications.ServerReachabilityChanged(Guid.NewGuid(), "srv", false),
            TestContext.Current.CancellationToken);
        await publisher.PublishAsync(() => throw new InvalidOperationException("lookup failed"),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BuilderReturningNullSkipsPublication()
    {
        var jobs = new CapturingJobs();
        var publisher = new BackgroundNotificationEventPublisher(jobs, new MaskingRedactor(),
            NullLogger<BackgroundNotificationEventPublisher>.Instance);

        await publisher.PublishAsync(() => null, TestContext.Current.CancellationToken);

        Assert.Empty(jobs.Raised);
    }

    private sealed class CapturingJobs : IBackgroundJobDispatcher
    {
        public List<(Guid TeamId, RaiseNotificationRequest Request)> Raised { get; } = [];

        public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall)
        {
            Assert.Equal(typeof(RaiseNotificationJob), typeof(TJob));
            var call = (MethodCallExpression)methodCall.Body;
            var teamId = (Guid)Evaluate(call.Arguments[0])!;
            var request = (RaiseNotificationRequest)Evaluate(call.Arguments[1])!;
            Raised.Add((teamId, request));
            return "job";
        }

        public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay)
        {
            throw new NotSupportedException();
        }

        private static object? Evaluate(Expression expression)
        {
            return Expression.Lambda(expression).Compile().DynamicInvoke();
        }
    }

    private sealed class ThrowingJobs : IBackgroundJobDispatcher
    {
        public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall)
        {
            throw new InvalidOperationException("Hangfire storage is unavailable.");
        }

        public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay)
        {
            throw new InvalidOperationException("Hangfire storage is unavailable.");
        }
    }

    private sealed class MaskingRedactor : ISecretRedactor
    {
        public string Redact(string value, RedactionContext? context = null)
        {
            return value.Replace("s3cr3t", "***", StringComparison.Ordinal);
        }

        public byte[] RedactUtf8(byte[] value, RedactionContext? context = null)
        {
            return value;
        }
    }
}
