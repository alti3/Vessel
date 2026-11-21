using System.Net;
using System.Text;
using System.Text.Json;
using Vessel.Application.Email;
using Vessel.Application.Notifications;
using Vessel.Domain.Notifications;
using Vessel.Domain.Settings;
using Vessel.Infrastructure.Email;
using Vessel.Infrastructure.Notifications;
using Vessel.Infrastructure.OutboundRequests;

namespace Vessel.IntegrationTests.Email;

/// <summary>Resend transport (NTF-06) and provider response classification (NTF-03).</summary>
public sealed class ResendAndProviderClassificationTests
{
    [Fact]
    public async Task ResendRequestUsesBearerKeyAndUpstreamFieldShape()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"id":"msg_123"}""");
        var transport = new ResendEmailTransport(new HttpClient(handler) { BaseAddress = new Uri("https://api.resend.com/") });

        EmailSendResult result = await transport.SendAsync("re_secret", new EmailSender("mail@example.com", "Vessel"),
            new EmailMessage(["ops@example.com"], "Subject", "Text", "<p>Html</p>"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("msg_123", result.ProviderMessageId);
        Assert.Equal(new Uri("https://api.resend.com/emails"), handler.Request!.RequestUri);
        Assert.Equal("Bearer re_secret", handler.Request.Headers.Authorization!.ToString());
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("\"Vessel\" <mail@example.com>", body.RootElement.GetProperty("from").GetString());
        Assert.Equal("ops@example.com", body.RootElement.GetProperty("to")[0].GetString());
        Assert.Equal("<p>Html</p>", body.RootElement.GetProperty("html").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, NotificationFailureKind.Configuration)]
    [InlineData(HttpStatusCode.Forbidden, NotificationFailureKind.Configuration)]
    [InlineData(HttpStatusCode.UnprocessableEntity, NotificationFailureKind.Payload)]
    [InlineData(HttpStatusCode.TooManyRequests, NotificationFailureKind.Transient)]
    [InlineData(HttpStatusCode.BadGateway, NotificationFailureKind.Transient)]
    public async Task ResendFailuresAreClassifiedWithoutTheResponseBody(HttpStatusCode status,
        NotificationFailureKind kind)
    {
        var handler = new RecordingHandler(status, """{"message":"invalid key re_secret for ops@example.com"}""");
        var transport = new ResendEmailTransport(new HttpClient(handler) { BaseAddress = new Uri("https://api.resend.com/") });

        EmailSendResult result = await transport.SendAsync("re_secret", new EmailSender("mail@example.com", null),
            new EmailMessage(["ops@example.com"], "S", "T"), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(kind, result.FailureKind);
        Assert.DoesNotContain("re_secret", result.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain("ops@example.com", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResendSuccessWithoutAnIdIsNotCountedAsSent()
    {
        var transport = new ResendEmailTransport(new HttpClient(new RecordingHandler(HttpStatusCode.OK, "{}"))
        {
            BaseAddress = new Uri("https://api.resend.com/")
        });

        EmailSendResult result = await transport.SendAsync("re_secret", new EmailSender("mail@example.com", null),
            new EmailMessage(["ops@example.com"], "S", "T"), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, NotificationFailureKind.Configuration)]
    [InlineData(HttpStatusCode.Unauthorized, NotificationFailureKind.Configuration)]
    [InlineData(HttpStatusCode.BadRequest, NotificationFailureKind.Payload)]
    [InlineData(HttpStatusCode.TooManyRequests, NotificationFailureKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, NotificationFailureKind.Transient)]
    public async Task WebhookResponsesAreClassified(HttpStatusCode status, NotificationFailureKind kind)
    {
        var provider = new HttpWebhookNotificationProvider(new HttpClient(new RecordingHandler(status, "secret body")));

        NotificationDeliveryResult result = await provider.SendAsync(Message(),
            Context(NotificationChannel.Webhook, """{"url":"https://hooks.example.com/top-secret"}"""),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(kind, result.FailureKind);
        Assert.DoesNotContain("secret", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlockedOutboundTargetIsAConfigurationError()
    {
        var provider = new SlackNotificationProvider(new HttpClient(new ThrowingHandler(
            new HttpRequestException("wrapped", new OutboundRequestBlockedException("loopback")))));

        NotificationDeliveryResult result = await provider.SendAsync(Message(),
            Context(NotificationChannel.Slack, """{"webhookUrl":"https://hooks.slack.com/services/top-secret"}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationFailureKind.Configuration, result.FailureKind);
        Assert.DoesNotContain("top-secret", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkErrorsAndTimeoutsAreTransient()
    {
        var provider = new DiscordNotificationProvider(new HttpClient(new ThrowingHandler(
            new TaskCanceledException("timeout"))));

        NotificationDeliveryResult result = await provider.SendAsync(Message(),
            Context(NotificationChannel.Discord, """{"webhookUrl":"https://discord.com/api/webhooks/1/top-secret"}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationFailureKind.Transient, result.FailureKind);
    }

    [Fact]
    public async Task TelegramChatNotFoundIsAConfigurationErrorAndRoutesUseTopics()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadRequest,
            """{"ok":false,"description":"Bad Request: chat not found"}""");
        var provider = new TelegramNotificationProvider(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.telegram.org")
        });

        NotificationDeliveryResult result = await provider.SendAsync(Message(),
            Context(NotificationChannel.Telegram,
                """{"botToken":"123456:ABCDEFGHIJKLMNOPQRSTUVWXYZ","chatId":"-100","threadId":"5"}""", "99"),
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationFailureKind.Configuration, result.FailureKind);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("99", body.RootElement.GetProperty("message_thread_id").GetString());
    }

    [Theory]
    [InlineData("""{"smtpHost":"smtp.example.com","smtpPort":465,"from":"a@example.com","to":"b@example.com"}""",
        SmtpEncryption.Tls)]
    [InlineData("""{"smtpHost":"smtp.example.com","smtpPort":587,"from":"a@example.com","to":"b@example.com"}""",
        SmtpEncryption.StartTls)]
    [InlineData("""{"smtpHost":"smtp.example.com","smtpPort":25,"enableSsl":false,"from":"a@example.com","to":"b@example.com"}""",
        SmtpEncryption.None)]
    [InlineData("""{"smtpHost":"smtp.example.com","smtpPort":2465,"encryption":"tls","from":"a@example.com","to":"b@example.com"}""",
        SmtpEncryption.Tls)]
    public async Task EmailTargetsMapLegacyAndExplicitEncryption(string secret, SmtpEncryption expected)
    {
        var smtp = new CapturingSmtp();
        var provider = new EmailNotificationProvider(smtp, null!);

        NotificationDeliveryResult result = await provider.SendAsync(Message(), Context(NotificationChannel.Email, secret),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(expected, smtp.Settings!.Encryption);
        Assert.Equal(["b@example.com"], smtp.Message!.To);
        Assert.Contains("https://vessel.example.com/deployments/1", smtp.Message.TextBody, StringComparison.Ordinal);
    }

    private static NotificationEventDispatchMessage Message()
    {
        return new NotificationEventDispatchMessage(Guid.NewGuid(), NotificationEventCatalog.DeploymentFailed,
            NotificationSeverity.Error, NotificationTargetType.Deployment, "1", "Deployment failed", "It failed.", "{}",
            "https://vessel.example.com/deployments/1", DateTimeOffset.UtcNow);
    }

    private static NotificationTargetDispatchContext Context(NotificationChannel channel, string secret,
        string? route = null)
    {
        return new NotificationTargetDispatchContext(Guid.NewGuid(), "Ops", channel, "{}", secret, route);
    }

    private sealed class RecordingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw exception;
        }
    }

    private sealed class CapturingSmtp : ISmtpEmailTransport
    {
        public SmtpTransportSettings? Settings { get; private set; }
        public EmailMessage? Message { get; private set; }

        public Task<EmailSendResult> SendAsync(SmtpTransportSettings settings, EmailSender from, EmailMessage message,
            CancellationToken cancellationToken = default)
        {
            Settings = settings;
            Message = message;
            return Task.FromResult(EmailSendResult.Sent());
        }
    }
}
