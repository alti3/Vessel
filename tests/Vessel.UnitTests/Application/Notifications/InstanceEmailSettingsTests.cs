using System.Net;
using System.Text.Json;
using Vessel.Application.Auditing;
using Vessel.Application.Email;
using Vessel.Application.OutboundRequests;
using Vessel.Domain.Common;
using Vessel.Domain.Notifications;
using Vessel.Domain.Settings;

namespace Vessel.UnitTests.Application.Notifications;

/// <summary>NTF-06 instance email settings: admin-only, write-only secrets, sender split, SEC-08 host checks.</summary>
public sealed class InstanceEmailSettingsTests
{
    private const string SmtpPassword = "smtp-pa55word-top-secret";
    private const string ResendKey = "re_top_secret_key";

    [Fact]
    public async Task OnlyInstanceAdministratorsCanReadOrChangeEmailSettings()
    {
        var kit = NotificationTestKit.Create();
        var (service, _) = Create(kit);

        // The team owner is not an instance administrator.
        Assert.Throws<UnauthorizedAccessException>(() => service.Get(kit.Db.Owner.Id));
        Assert.Throws<UnauthorizedAccessException>(() => service.ListPresets(kit.Db.Member.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateAsync(kit.Db.Owner.Id,
            SmtpRequest(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SendTestAsync(kit.Db.Owner.Id,
            "owner@example.com", TestContext.Current.CancellationToken));
        Assert.Empty(kit.Db.EmailSettings);
    }

    [Fact]
    public async Task SecretsAreEncryptedAndNeverReturned()
    {
        var kit = NotificationTestKit.Create();
        kit.Db.Owner.GrantInstanceAdmin(kit.Time.GetUtcNow());
        var (service, _) = Create(kit);

        var view = await service.UpdateAsync(kit.Db.Owner.Id, SmtpRequest() with { ResendApiKey = ResendKey },
            TestContext.Current.CancellationToken);

        var settings = Assert.Single(kit.Db.EmailSettings);
        Assert.DoesNotContain(SmtpPassword, settings.SmtpServers[0].ProtectedPassword, StringComparison.Ordinal);
        Assert.Equal(SmtpPassword, kit.Cipher.Unprotect(settings.SmtpServers[0].ProtectedPassword!,
            InstanceEmailSender.SmtpPasswordPurpose));
        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain(SmtpPassword, json, StringComparison.Ordinal);
        Assert.DoesNotContain(ResendKey, json, StringComparison.Ordinal);
        Assert.Contains("\"smtp_password_set\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"resend_api_key_set\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"resend_from_address\"", json, StringComparison.Ordinal);
        Assert.Contains("\"smtp_ehlo_domain\":\"mx.example.com\"", json, StringComparison.Ordinal);

        var audit = Assert.Single(kit.Audit.Records, record => record.Action == AuditActions.InstanceEmailSettingsUpdated);
        Assert.DoesNotContain(SmtpPassword, JsonSerializer.Serialize(audit.Metadata), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OmittedPasswordKeepsTheStoredOneAndEmptyClearsIt()
    {
        var kit = NotificationTestKit.Create();
        kit.Db.Owner.GrantInstanceAdmin(kit.Time.GetUtcNow());
        var (service, _) = Create(kit);
        await service.UpdateAsync(kit.Db.Owner.Id, SmtpRequest(), TestContext.Current.CancellationToken);
        var stored = kit.Db.EmailSettings[0].SmtpServers[0].ProtectedPassword;

        await service.UpdateAsync(kit.Db.Owner.Id, new UpdateInstanceEmailSettingsRequest(SmtpServers:
        [
            new SmtpServerInput("Default", Host: "smtp.example.com", Port: 587, FromAddress: "noreply@example.com")
        ]), TestContext.Current.CancellationToken);
        Assert.Equal(stored, kit.Db.EmailSettings[0].SmtpServers[0].ProtectedPassword);

        await service.UpdateAsync(kit.Db.Owner.Id, new UpdateInstanceEmailSettingsRequest(SmtpServers:
        [
            new SmtpServerInput("Default", Password: "")
        ]), TestContext.Current.CancellationToken);
        Assert.Null(kit.Db.EmailSettings[0].SmtpServers[0].ProtectedPassword);
    }

    [Fact]
    public async Task SmtpHostsRefusedByTheOutboundPolicyAreNotSaved()
    {
        var kit = NotificationTestKit.Create();
        kit.Db.Owner.GrantInstanceAdmin(kit.Time.GetUtcNow());
        var (service, policy) = Create(kit);
        policy.Deny = "the host resolves to a loopback address.";

        var exception = await Assert.ThrowsAsync<DomainException>(() => service.UpdateAsync(kit.Db.Owner.Id,
            SmtpRequest(), TestContext.Current.CancellationToken));

        Assert.Contains("loopback", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SmtpPassword, exception.Message, StringComparison.Ordinal);
        Assert.Empty(kit.Db.EmailSettings);
    }

    [Fact]
    public async Task PresetsFillConnectionFieldsIncludingImplicitTls()
    {
        var kit = NotificationTestKit.Create();
        kit.Db.Owner.GrantInstanceAdmin(kit.Time.GetUtcNow());
        var (service, _) = Create(kit);

        var view = await service.UpdateAsync(kit.Db.Owner.Id, new UpdateInstanceEmailSettingsRequest("smtp",
            SmtpServers: [new SmtpServerInput("Resend SMTP", "resend", Password: "x", FromAddress: "a@example.com")]),
            TestContext.Current.CancellationToken);

        var server = Assert.Single(view.SmtpServers);
        Assert.Equal(("smtp.resend.com", 465, "tls", "resend"), (server.Host, server.Port, server.Encryption,
            server.Username));
        Assert.Equal(12, service.ListPresets(kit.Db.Owner.Id).Count);
    }

    [Fact]
    public async Task SenderSplitUsesNotificationSenderOnlyForNotifications()
    {
        var kit = NotificationTestKit.Create();
        kit.ConfigureInstanceSmtp("alerts@example.com");
        var message = new EmailMessage(["ops@example.com"], "Subject", "Body");

        await kit.Sender.SendAsync(message, EmailPurpose.Transactional, TestContext.Current.CancellationToken);
        await kit.Sender.SendAsync(message, EmailPurpose.Notification, TestContext.Current.CancellationToken);

        Assert.Equal("noreply@example.com", kit.Smtp.Sent[0].From.Address);
        Assert.Equal("alerts@example.com", kit.Smtp.Sent[1].From.Address);
        Assert.Equal("Vessel Alerts", kit.Smtp.Sent[1].From.Name);
        var smtp = kit.Smtp.Sent[0].Smtp!;
        Assert.Equal((SmtpEncryption.Tls, 465, "vessel.example.com", "smtp-password"),
            (smtp.Encryption, smtp.Port, smtp.EhloDomain, smtp.Password));
        Assert.DoesNotContain("smtp-password", smtp.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResendProviderUsesResendSenderFields()
    {
        var kit = NotificationTestKit.Create();
        var settings = InstanceEmailSettings.Create(kit.Time.GetUtcNow());
        settings.Update(EmailProvider.Resend, null, [],
            kit.Cipher.Protect(ResendKey, InstanceEmailSender.ResendApiKeyPurpose), "mail@example.com", "Vessel Mail",
            null, null, kit.Time.GetUtcNow());
        kit.Db.EmailSettings.Add(settings);

        var result = await kit.Sender.SendAsync(new EmailMessage(["ops@example.com"], "S", "B"),
            EmailPurpose.Notification, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var sent = Assert.Single(kit.Resend.Sent);
        Assert.Equal(ResendKey, sent.ApiKey);
        Assert.Equal(new EmailSender("mail@example.com", "Vessel Mail"), sent.From);
        Assert.Empty(kit.Smtp.Sent);
    }

    [Fact]
    public async Task UnconfiguredInstanceEmailFailsAsConfigurationError()
    {
        var kit = NotificationTestKit.Create();

        var result = await kit.Sender.SendAsync(new EmailMessage(["a@example.com"], "S", "B"),
            EmailPurpose.Transactional, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(NotificationFailureKind.Configuration, result.FailureKind);
        Assert.False(kit.Sender.IsConfigured());
    }

    [Fact]
    public async Task QueuedMessagesAreEncryptedInJobArguments()
    {
        var kit = NotificationTestKit.Create();
        kit.Outbox.Enqueue(new EmailMessage(["a@example.com"], "Reset", "token=abc123secret"),
            EmailPurpose.Transactional);

        var job = Assert.Single(kit.Jobs.Enqueued);
        Assert.DoesNotContain("abc123secret", (string)job.Arguments[0]!, StringComparison.Ordinal);
        Assert.Equal("token=abc123secret", Assert.Single(kit.QueuedEmails()).TextBody);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SendJobRetriesTransientFailuresAndAuditsPermanentOnes()
    {
        var kit = NotificationTestKit.Create();
        kit.ConfigureInstanceSmtp();
        var job = new SendInstanceEmailJob(kit.Outbox, kit.Sender, kit.Audit);
        kit.Outbox.Enqueue(new EmailMessage(["a@example.com"], "S", "B"), EmailPurpose.Transactional);
        var payload = (string)kit.Jobs.Enqueued[0].Arguments[0]!;

        kit.Smtp.Result = EmailSendResult.Failed("Could not connect.", NotificationFailureKind.Transient);
        await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            job.RunAsync(payload, EmailPurpose.Transactional, TestContext.Current.CancellationToken));

        kit.Smtp.Result = EmailSendResult.Failed("Login refused.", NotificationFailureKind.Configuration);
        await job.RunAsync(payload, EmailPurpose.Transactional, TestContext.Current.CancellationToken);
        Assert.Contains(kit.Audit.Records, record => record.Action == "email.delivery_failed");

        await job.RunAsync("tampered", EmailPurpose.Transactional, TestContext.Current.CancellationToken);
    }

    private static (InstanceEmailSettingsService Service, TogglePolicy Policy) Create(NotificationTestKit kit)
    {
        var policy = new TogglePolicy();
        return (new InstanceEmailSettingsService(kit.Db, kit.Authorization, kit.Cipher, policy, kit.Sender, kit.Audit,
            kit.Time), policy);
    }

    private static UpdateInstanceEmailSettingsRequest SmtpRequest()
    {
        return new UpdateInstanceEmailSettingsRequest("smtp", "Default",
            [
                new SmtpServerInput("Default", Host: "smtp.example.com", Port: 587, Encryption: "starttls",
                    Username: "mailer", Password: SmtpPassword, FromAddress: "noreply@example.com",
                    FromName: "Vessel", EhloDomain: "mx.example.com", TimeoutSeconds: 20)
            ],
            ResendFromAddress: "mail@example.com", ResendFromName: "Vessel Mail",
            NotificationFromAddress: "alerts@example.com", NotificationFromName: "Vessel Alerts");
    }

    private sealed class TogglePolicy : IOutboundUrlPolicy
    {
        public string? Deny { get; set; }

        public Task<OutboundTargetCheck> CheckUrlAsync(Uri url, CancellationToken cancellationToken = default)
        {
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
}
