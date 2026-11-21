using Microsoft.Extensions.Logging.Abstractions;
using Vessel.Application.Notifications;
using Vessel.Application.Redis;
using Vessel.Application.Security.ApiAbuse;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Webhooks;

namespace Vessel.UnitTests.Security;

/// <summary>SEC-13 failed-request throttle and SEC-07 manual webhook secret strength.</summary>
public sealed class AbuseThrottleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(401, true, true)]
    [InlineData(403, true, true)]
    [InlineData(404, false, true)]
    [InlineData(404, true, false)]
    [InlineData(422, false, false)]
    [InlineData(429, false, false)]
    [InlineData(200, false, false)]
    public void CountedFailures_MatchUpstream(int status, bool hasToken, bool counted)
    {
        Assert.Equal(counted, ApiFailedRequestThrottle.IsCountedFailure(status, hasToken));
    }

    [Fact]
    public async Task Client_IsBlockedAtTheLimit_AndTheTokensTeamIsNotifiedOncePerDay()
    {
        var time = new MutableTime(DateTimeOffset.Parse("2026-11-20T12:00:05Z"));
        var publisher = new RecordingPublisher();
        ApiFailedRequestThrottle throttle = Create(time, publisher, limit: 3);
        var teamId = TeamId.New();
        var client = new ApiClient("203.0.113.9", PersonalAccessTokenId.New(), teamId);

        Assert.Null(await throttle.RecordFailureAsync(client, "GET /api/v1/projects -> 403", Ct));
        Assert.Null(await throttle.RecordFailureAsync(client, "GET /api/v1/projects -> 403", Ct));
        Assert.Null(await throttle.GetBlockAsync(client, Ct));
        Assert.Equal(TimeSpan.FromSeconds(60), await throttle.RecordFailureAsync(client, "GET /x -> 403", Ct));

        Assert.NotNull(await throttle.GetBlockAsync(client, Ct));
        Assert.Null(await throttle.GetBlockAsync(new ApiClient("203.0.113.9", null, null), Ct));
        (TeamId notifiedTeam, RaiseNotificationRequest notification) = Assert.Single(publisher.Published);
        Assert.Equal(teamId, notifiedTeam);
        Assert.Equal(NotificationEventTypes.ApiTokenFailing, notification.EventType);

        // After the block ends, reaching the limit again escalates the block and does not notify again today.
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(await throttle.GetBlockAsync(client, Ct));
        await throttle.RecordFailureAsync(client, "GET /x -> 403", Ct);
        await throttle.RecordFailureAsync(client, "GET /x -> 403", Ct);
        Assert.Equal(TimeSpan.FromSeconds(300), await throttle.RecordFailureAsync(client, "GET /x -> 403", Ct));
        Assert.Single(publisher.Published);
    }

    [Fact]
    public async Task Throttle_IsDisabledWithAZeroLimit()
    {
        ApiFailedRequestThrottle throttle = Create(new MutableTime(DateTimeOffset.UtcNow), new RecordingPublisher(),
            limit: 0);
        var client = new ApiClient("198.51.100.4", null, null);

        for (var i = 0; i < 50; i++) Assert.Null(await throttle.RecordFailureAsync(client, "x", Ct));
        Assert.False(throttle.Enabled);
        Assert.Null(await throttle.GetBlockAsync(client, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short-secret")]
    [InlineData("fifteen-chars!!")]
    public void WebhookSecret_RequiresAtLeastSixteenCharacters(string value)
    {
        Assert.Throws<DomainException>(() => new WebhookSecret(value));
    }

    [Fact]
    public void WebhookSecret_AcceptsSixteenCharacters_AndNeverRendersTheValue()
    {
        var secret = new WebhookSecret("sixteen-chars-ok");

        Assert.Equal("sixteen-chars-ok", secret.Value);
        Assert.Equal("[redacted]", secret.ToString());
        Assert.Throws<DomainException>(() => new WebhookSecret(new string('x', WebhookSecret.MaxLength + 1)));
    }

    private static ApiFailedRequestThrottle Create(TimeProvider time, INotificationEventPublisher publisher,
        int limit)
    {
        return new ApiFailedRequestThrottle(new InMemoryRedisCache(time), time, publisher,
            new ApiFailedRequestThrottleSettings(limit, TimeSpan.FromSeconds(60)),
            NullLogger<ApiFailedRequestThrottle>.Instance);
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by)
        {
            _now = _now.Add(by);
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }
    }

    private sealed class RecordingPublisher : INotificationEventPublisher
    {
        public List<(TeamId, RaiseNotificationRequest)> Published { get; } = [];

        public Task PublishAsync(TeamId teamId, RaiseNotificationRequest notification,
            CancellationToken cancellationToken = default)
        {
            Published.Add((teamId, notification));
            return Task.CompletedTask;
        }

        public Task PublishAsync(Func<TeamNotification?> build, CancellationToken cancellationToken = default)
        {
            TeamNotification? notification = build();
            if (notification is not null) Published.Add((notification.TeamId, notification.Notification));
            return Task.CompletedTask;
        }
    }
}
