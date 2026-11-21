using System.Text.Json;
using Microsoft.Extensions.Options;
using Vessel.Application.Auth;
using Vessel.Application.Common;
using Vessel.Application.Email;
using Vessel.Application.Security;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.UnitTests.Security;

namespace Vessel.UnitTests.Application.Notifications;

/// <summary>IAM-10: password reset, invitation, and verification mail through the instance mailer.</summary>
public sealed class AuthEmailDeliveryTests
{
    [Fact]
    public async Task PasswordResetTokenIsEmailedWithALinkFromThePublicUrl()
    {
        var kit = NotificationTestKit.Create(publicUrl: "https://vessel.example.com");
        kit.ConfigureInstanceSmtp();
        var tokens = new SequentialTokenGenerator();
        var auth = CreateAuthentication(kit, tokens);

        await auth.RequestPasswordResetAsync("member@example.com", null, TestContext.Current.CancellationToken);

        var email = Assert.Single(kit.QueuedEmails());
        Assert.Equal(["member@example.com"], email.To);
        var link = ExtractLink(email.TextBody);
        Assert.StartsWith("https://vessel.example.com/password-reset?email=member%40example.com&token=", link,
            StringComparison.Ordinal);
        var token = Uri.UnescapeDataString(link[(link.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        Assert.Equal(TokenHashing.Sha256(token), kit.Db.Member.PasswordResetTokenHash);
        Assert.Contains(WebEncoded(link), email.HtmlBody, StringComparison.Ordinal);

        // The token never reaches the audit trail or the (persisted) job arguments.
        Assert.DoesNotContain(token, JsonSerializer.Serialize(kit.Audit.Records), StringComparison.Ordinal);
        Assert.DoesNotContain(kit.Jobs.Enqueued, job => job.Arguments.OfType<string>()
            .Any(argument => argument.Contains(token, StringComparison.Ordinal)));
        Assert.True(await auth.ResetPasswordAsync("member@example.com", token, "N3w-Passw0rd-Long!", null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NothingIsSentWithoutPublicUrlOrInstanceEmail()
    {
        var noUrl = NotificationTestKit.Create(publicUrl: null);
        noUrl.ConfigureInstanceSmtp();
        await CreateAuthentication(noUrl).RequestPasswordResetAsync("member@example.com", null,
            TestContext.Current.CancellationToken);
        Assert.Empty(noUrl.Jobs.Enqueued);
        Assert.NotNull(noUrl.Db.Member.PasswordResetTokenHash);

        var noMail = NotificationTestKit.Create();
        await CreateAuthentication(noMail).RequestPasswordResetAsync("member@example.com", null,
            TestContext.Current.CancellationToken);
        Assert.Empty(noMail.Jobs.Enqueued);
    }

    [Fact]
    public async Task UnknownAccountsGetNoEmailAndTheSameResponse()
    {
        var kit = NotificationTestKit.Create();
        kit.ConfigureInstanceSmtp();

        await CreateAuthentication(kit).RequestPasswordResetAsync("nobody@example.com", null,
            TestContext.Current.CancellationToken);

        Assert.Empty(kit.QueuedEmails());
    }

    [Fact]
    public void PublicUrlMustBeAPlainAbsoluteHttpUrl()
    {
        Assert.NotNull(InstanceUrlOptions.ParseBaseUrl("https://vessel.example.com"));
        Assert.Equal("https://vessel.example.com/sub/", InstanceUrlOptions.ParseBaseUrl("https://vessel.example.com/sub")!
            .AbsoluteUri);
        Assert.Null(InstanceUrlOptions.ParseBaseUrl("javascript:alert(1)"));
        Assert.Null(InstanceUrlOptions.ParseBaseUrl("https://user:pw@vessel.example.com"));
        Assert.Null(InstanceUrlOptions.ParseBaseUrl("https://vessel.example.com/?next=evil"));
        Assert.Null(InstanceUrlOptions.ParseBaseUrl("/relative"));

        var links = new InstanceLinkBuilder(Options.Create(new InstanceUrlOptions { PublicUrl = "https://v.example" }));
        Assert.Null(links.Build("//evil.example/x"));
        Assert.Null(links.Build("https://evil.example/x"));
        Assert.Equal("https://v.example/a?x=1%262", links.Build("/a", new Dictionary<string, string> { ["x"] = "1&2" })!
            .AbsoluteUri);
    }

    [Fact]
    public async Task InvitationLinkIsEmailedToTheInvitee()
    {
        var kit = NotificationTestKit.Create();
        kit.ConfigureInstanceSmtp();
        var teams = new VesselTeamService(kit.Db, new SequentialTokenGenerator(), kit.Audit, CreateAuthEmails(kit));

        var result = await teams.InviteAsync(kit.Db.Owner.Id, kit.Db.TeamId, "new@example.com", TeamRole.Member,
            kit.Time.GetUtcNow().AddDays(7), null, TestContext.Current.CancellationToken);

        var email = Assert.Single(kit.QueuedEmails());
        Assert.Equal(["new@example.com"], email.To);
        Assert.Contains($"https://vessel.example.com/invitations/accept?token={Uri.EscapeDataString(result.PlainTextToken)}",
            email.TextBody, StringComparison.Ordinal);
        Assert.Contains("Team", email.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistrationSendsAVerificationEmailThatConfirmsTheAddress()
    {
        var kit = NotificationTestKit.Create();
        kit.ConfigureInstanceSmtp();
        var authEmails = CreateAuthEmails(kit);
        var auth = CreateAuthentication(kit, authEmails: authEmails);
        // IAM-06: self-registration is closed once users exist; a pending invitation keeps it open for this email.
        kit.Db.Invitations.Add(Vessel.Domain.Teams.TeamInvitation.Create(kit.Db.Team.Id,
            new Vessel.Domain.ValueObjects.EmailAddress("new@example.com"), Vessel.Domain.Teams.TeamRole.Member,
            "invitation-hash", kit.Time.GetUtcNow().AddDays(1), kit.Time.GetUtcNow()));

        var registered = await auth.RegisterAsync("New", "new@example.com", "N3w-Passw0rd-Long!", null,
            TestContext.Current.CancellationToken);

        var email = Assert.Single(kit.QueuedEmails());
        var link = ExtractLink(email.TextBody);
        Assert.StartsWith("https://vessel.example.com/verify-email?token=", link, StringComparison.Ordinal);
        var token = Uri.UnescapeDataString(link[(link.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        var user = kit.Db.UsersList.Single(item => item.Id == registered.UserId);

        Assert.False(await authEmails.ConfirmEmailVerificationAsync(token + "x", null,
            TestContext.Current.CancellationToken));
        Assert.Null(user.EmailVerifiedAt);
        Assert.True(await authEmails.ConfirmEmailVerificationAsync(token, null, TestContext.Current.CancellationToken));
        Assert.NotNull(user.EmailVerifiedAt);
    }

    [Fact]
    public async Task VerificationTokensExpireAndAreBoundToTheUser()
    {
        var kit = NotificationTestKit.Create();
        var authEmails = CreateAuthEmails(kit);
        var token = authEmails.CreateVerificationToken(kit.Db.Member,
            kit.Time.GetUtcNow().Add(AuthEmailService.VerificationTokenLifetime));

        kit.Time.Advance(TimeSpan.FromHours(25));
        Assert.False(await authEmails.ConfirmEmailVerificationAsync(token, null, TestContext.Current.CancellationToken));

        // A token for another user cannot be replayed through a different purpose either.
        var foreign = kit.Cipher.Protect($"{kit.Db.Owner.Id.Value:N}|member@example.com|9999999999",
            "vessel:auth:email-verification");
        Assert.False(await authEmails.ConfirmEmailVerificationAsync(foreign, null,
            TestContext.Current.CancellationToken));
        Assert.Null(kit.Db.Member.EmailVerifiedAt);
        Assert.Null(kit.Db.Owner.EmailVerifiedAt);
    }

    private static AuthEmailService CreateAuthEmails(NotificationTestKit kit)
    {
        return new AuthEmailService(kit.Db, kit.Outbox, kit.Sender, kit.Links, kit.Cipher, kit.Audit, kit.Time);
    }

    private static VesselAuthenticationService CreateAuthentication(NotificationTestKit kit,
        ITokenGenerator? tokens = null, AuthEmailService? authEmails = null)
    {
        var generator = tokens ?? new SequentialTokenGenerator();
        kit.Db.Member.SetPasswordHash(new FakePasswordHasher().HashPassword("old"), false, kit.Time.GetUtcNow());
        return new VesselAuthenticationService(kit.Db, new FakePasswordHasher(), generator, new TotpService(generator),
            kit.Audit, Options.Create(new AuthOptions()), kit.Cipher, authEmails ?? CreateAuthEmails(kit));
    }

    private static string ExtractLink(string text)
    {
        return text.Split('\n').Single(line => line.StartsWith("https://", StringComparison.Ordinal)).Trim();
    }

    private static string WebEncoded(string value)
    {
        return System.Net.WebUtility.HtmlEncode(value);
    }
}
