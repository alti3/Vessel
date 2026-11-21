using Microsoft.Extensions.Options;
using Vessel.Application.Auditing;
using Vessel.Application.Auth;
using Vessel.Application.Auth.External;
using Vessel.Application.Auth.Sessions;
using Vessel.Application.Authorization;
using Vessel.Application.Common;
using Vessel.Application.Instance;
using Vessel.Application.Notifications;
using Vessel.Application.OutboundRequests;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Auth;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;
using Vessel.UnitTests.Application.SecurityHardening;

namespace Vessel.UnitTests.Security;

/// <summary>Wave 2 identity items: IAM-04 expiry warnings, IAM-07 external sign-in, IAM-08 audit, IAM-09 sessions.</summary>
public sealed class IdentityWave2Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- IAM-08 audit redaction, levels, query, anonymization ----------

    [Fact]
    public void AuditRedaction_RemovesSecretsMasksEmailsAndStripsUrlCredentials()
    {
        Dictionary<string, object?> redacted = AuditRedaction.RedactMetadata(new Dictionary<string, object?>
        {
            ["password"] = "hunter2",
            ["clientSecret"] = "s3cr3t",
            ["apiKey"] = "abc",
            ["tokenId"] = "11111111-1111-1111-1111-111111111111",
            ["email"] = "jane@example.com",
            ["note"] = "contact bob@example.org via https://user:pw@git.example.com/repo.git",
            ["command"] = "docker run -e SECRET=1",
            ["revokedApiCredentials"] = 3,
            ["auth"] = "Bearer abc.def.ghi"
        });

        Assert.Equal(AuditRedaction.Redacted, redacted["password"]);
        Assert.Equal(AuditRedaction.Redacted, redacted["clientSecret"]);
        Assert.Equal(AuditRedaction.Redacted, redacted["apiKey"]);
        Assert.Equal("11111111-1111-1111-1111-111111111111", redacted["tokenId"]);
        Assert.Equal("j***@example.com", redacted["email"]);
        var note = Assert.IsType<string>(redacted["note"]);
        Assert.DoesNotContain("pw@", note, StringComparison.Ordinal);
        Assert.DoesNotContain("bob@example.org", note, StringComparison.Ordinal);
        Assert.Equal(AuditRedaction.Omitted, redacted["command"]);
        Assert.Equal(3, redacted["revokedApiCredentials"]);
        Assert.DoesNotContain("abc.def", (string)redacted["auth"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditChanges_SensitiveFieldsAreRecordedWithoutValues()
    {
        IReadOnlyList<AuditFieldChange> changes = AuditChanges.Between(
            new Dictionary<string, object?> { ["name"] = "a", ["clientSecret"] = "old", ["same"] = 1 },
            new Dictionary<string, object?> { ["name"] = "b", ["clientSecret"] = "new", ["same"] = 1 });
        Dictionary<string, object?> redacted = AuditRedaction.RedactChanges(changes)!;

        Assert.Equal(2, redacted.Count);
        var secret = Assert.IsType<Dictionary<string, object?>>(redacted["clientSecret"]);
        Assert.Equal(AuditRedaction.Changed, secret["before"]);
        Assert.Equal(AuditRedaction.Changed, secret["after"]);
        var name = Assert.IsType<Dictionary<string, object?>>(redacted["name"]);
        Assert.Equal("a", name["before"]);
    }

    [Theory]
    [InlineData(AuditActions.UserLoginFailed, AuditLevel.Warning)]
    [InlineData(AuditActions.OAuthLoginDenied, AuditLevel.Warning)]
    [InlineData(AuditActions.RegistrationRejected, AuditLevel.Warning)]
    [InlineData(AuditActions.UserLoggedIn, AuditLevel.Info)]
    [InlineData(AuditActions.TokenRevoked, AuditLevel.Info)]
    public void AuditLevels_FailuresAndDenialsAreWarnings(string action, AuditLevel expected)
    {
        Assert.Equal(expected, AuditLevels.Classify(action));
    }

    [Fact]
    public void AuditQuery_OnlyOwnersAndAdminsOfTheTeam_SeeOnlyThatTeam()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        fixture.Store.Set<AuditLog>().AddRange([
            Audit(fixture.TeamA.Id, "project.created"),
            Audit(fixture.TeamB.Id, "project.created"),
            Audit(fixture.TeamA.Id, "auth.login_failed")
        ]);
        var service = new AuditQueryService(fixture.Store.Context, fixture.Authorization);

        // Actor is Admin in team A: sees team A only.
        AuditEventPage page = service.Query(fixture.Actor.Id, fixture.TeamA.Id, new AuditEventQuery());
        Assert.Equal(2, page.Total);
        Assert.All(page.Items, item => Assert.Equal(fixture.TeamA.Id.Value, item.TeamId));
        Assert.True(page.IncludesSensitiveFields);

        AuditEventPage filtered = service.Query(fixture.Actor.Id, fixture.TeamA.Id,
            new AuditEventQuery(Action: "auth.login_failed"));
        Assert.Single(filtered.Items);

        // Actor is only a Member in team B: denied.
        Assert.Throws<UnauthorizedAccessException>(() =>
            service.Query(fixture.Actor.Id, fixture.TeamB.Id, new AuditEventQuery()));
        Assert.Throws<ArgumentException>(() =>
            service.Query(fixture.Actor.Id, fixture.TeamA.Id, new AuditEventQuery(PerPage: 101)));
    }

    [Fact]
    public void AuditQuery_ReadOnlyToken_GetsNoSensitiveFields()
    {
        CrossTeamFixture fixture = CrossTeamFixture.Create();
        AuditLog row = Audit(fixture.TeamA.Id, "project.created")
            .WithContext(AuditSources.Api, AuditActorTypes.User, "Actor", null, null, "203.0.113.9", "curl", AuditLevel.Info);
        fixture.Store.Set<AuditLog>().Add(row);
        var tokenContext = new FixedTokenContext(new ApiTokenAccess(PersonalAccessTokenId.New(), fixture.Actor.Id,
            fixture.TeamA.Id, new HashSet<string> { TokenScopeMapper.Read }, null));
        var authorization = new VesselAuthorizationService(fixture.Store.Context,
            new AccessResolver(fixture.Store.Context, tokenContext), tokenContext);

        AuditEventPage page = new AuditQueryService(fixture.Store.Context, authorization)
            .Query(fixture.Actor.Id, fixture.TeamA.Id, new AuditEventQuery());

        AuditEventSummary item = Assert.Single(page.Items);
        Assert.False(page.IncludesSensitiveFields);
        Assert.Null(item.IpAddress);
        Assert.Null(item.UserAgent);
        Assert.Null(item.Metadata);
    }

    [Fact]
    public async Task AuditAnonymization_RemovesActorPersonalData()
    {
        InMemoryVesselDb store = InMemoryVesselDb.Create();
        var userId = UserId.New();
        AuditLog performed = AuditLog.Record(null, userId, "project.created", new AuditTarget("project", "p"), null,
                "{\"email\":\"j***@example.com\",\"name\":\"Jane\"}", DateTimeOffset.UtcNow)
            .WithContext(AuditSources.Ui, AuditActorTypes.User, "Jane", null, null, "198.51.100.1", "browser",
                AuditLevel.Info);
        AuditLog about = AuditLog.Record(null, null, "team.member_removed", new AuditTarget("user", userId.ToString()),
            null, "{}", DateTimeOffset.UtcNow).WithDetails("Jane", null, null);
        store.Set<AuditLog>().AddRange([performed, about]);

        var changed = await new AuditAnonymizationService(store.Context, TimeProvider.System)
            .AnonymizeUserAsync(userId, "Jane", "jane@example.com", Ct);

        Assert.Equal(2, changed);
        Assert.Equal(AuditLog.DeletedUserName, performed.ActorName);
        Assert.Null(performed.IpAddress);
        Assert.Null(performed.UserAgent);
        Assert.DoesNotContain("Jane", performed.RedactedMetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("j***@", performed.RedactedMetadataJson, StringComparison.Ordinal);
        Assert.Equal(AuditLog.DeletedUserName, about.ResourceName);
    }

    [Fact]
    public async Task AuditRetention_DeletesOnlyExpiredEvents()
    {
        InMemoryVesselDb store = InMemoryVesselDb.Create();
        AuditLog old = AuditLog.Record(null, null, "x", new AuditTarget("t", "1"), null, "{}",
            DateTimeOffset.UtcNow.AddDays(-91));
        AuditLog recent = AuditLog.Record(null, null, "x", new AuditTarget("t", "2"), null, "{}",
            DateTimeOffset.UtcNow.AddDays(-89));
        store.Set<AuditLog>().AddRange([old, recent]);
        var audit = new RecordingAudit();

        var deleted = await new AuditRetentionService(store.Context, audit, Options.Create(new AuditOptions()),
            TimeProvider.System).PruneAsync(Ct);

        Assert.Equal(1, deleted);
        Assert.Same(recent, Assert.Single(store.Set<AuditLog>()));
        Assert.Contains(audit.Entries, entry => entry.Action == AuditActions.AuditEventsPruned);
    }

    // ---------- IAM-09 sessions and active team ----------

    [Fact]
    public async Task Login_NeverLandsInATeamTheUserIsOnlyInvitedTo()
    {
        (InMemoryVesselDb store, User user, Team personal) = UserWithPersonalTeam();
        var other = Team.Create(new DisplayName("Inviting team"), UserId.New(), false, DateTimeOffset.UtcNow);
        store.Set<Team>().Add(other);
        store.Set<TeamInvitation>().Add(TeamInvitation.Create(other.Id, user.Email, TeamRole.Member, "hash",
            DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow));

        AuthenticatedUser? result = await Authentication(store).LoginAsync(user.Email.Value, "pw", null, null, Ct);

        Assert.NotNull(result);
        Assert.Equal(personal.Id, result.TeamId);
    }

    [Fact]
    public async Task Login_RestoresTheRememberedTeam_OnlyWhileStillAMember()
    {
        (InMemoryVesselDb store, User user, Team personal) = UserWithPersonalTeam();
        var shared = Team.Create(new DisplayName("Shared"), UserId.New(), false, DateTimeOffset.UtcNow);
        shared.AddMember(user.Id, TeamRole.Member, DateTimeOffset.UtcNow);
        store.Set<Team>().Add(shared);
        user.SetCurrentTeam(shared.Id);

        Assert.Equal(shared.Id, (await Authentication(store).LoginAsync(user.Email.Value, "pw", null, null, Ct))!.TeamId);

        shared.RemoveMember(user.Id, DateTimeOffset.UtcNow);
        Assert.Equal(personal.Id,
            (await Authentication(store).LoginAsync(user.Email.Value, "pw", null, null, Ct))!.TeamId);
    }

    [Fact]
    public async Task Sessions_RevocationAndMembershipAreRecheckedOnValidation()
    {
        (InMemoryVesselDb store, User user, Team personal) = UserWithPersonalTeam();
        var shared = Team.Create(new DisplayName("Shared"), UserId.New(), false, DateTimeOffset.UtcNow);
        shared.AddMember(user.Id, TeamRole.Admin, DateTimeOffset.UtcNow);
        store.Set<Team>().Add(shared);
        UserSessionService sessions = Sessions(store);

        UserSessionId id = await sessions.StartAsync(user.Id, shared.Id, "password", SessionClient.Unknown, Ct);
        Assert.Equal(shared.Id, (await sessions.ValidateAsync(id, user.Id, Ct)).ActiveTeamId);

        // Another user's id never validates the session.
        Assert.False((await sessions.ValidateAsync(id, UserId.New(), Ct)).IsActive);

        // Removed from the team: the session moves to the home team instead of keeping the old one.
        shared.RemoveMember(user.Id, DateTimeOffset.UtcNow);
        SessionValidationResult moved = await sessions.ValidateAsync(id, user.Id, Ct);
        Assert.True(moved.IsActive);
        Assert.Equal(personal.Id, moved.ActiveTeamId);

        Assert.True(await sessions.RevokeAsync(user.Id, id, UserSessionService.ReasonRevokedByUser, null, Ct));
        Assert.False((await sessions.ValidateAsync(id, user.Id, Ct)).IsActive);
        Assert.Empty(sessions.List(user.Id, null));
    }

    [Fact]
    public async Task Sessions_SwitchRequiresMembershipAndPersistsTheTeam()
    {
        (InMemoryVesselDb store, User user, Team personal) = UserWithPersonalTeam();
        var shared = Team.Create(new DisplayName("Shared"), UserId.New(), false, DateTimeOffset.UtcNow);
        var foreign = Team.Create(new DisplayName("Foreign"), UserId.New(), false, DateTimeOffset.UtcNow);
        shared.AddMember(user.Id, TeamRole.Member, DateTimeOffset.UtcNow);
        store.Set<Team>().AddRange([shared, foreign]);
        UserSessionService sessions = Sessions(store);
        UserSessionId id = await sessions.StartAsync(user.Id, personal.Id, "password", SessionClient.Unknown, Ct);

        Assert.False(await sessions.SwitchTeamAsync(id, user.Id, foreign.Id, null, Ct));
        Assert.True(await sessions.SwitchTeamAsync(id, user.Id, shared.Id, null, Ct));
        Assert.Equal(shared.Id, user.CurrentTeamId);
        Assert.Equal(shared.Id, (await sessions.ValidateAsync(id, user.Id, Ct)).ActiveTeamId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            sessions.StartAsync(user.Id, foreign.Id, "password", SessionClient.Unknown, Ct));
    }

    [Fact]
    public async Task PasswordReset_RevokesEverySessionAndToken()
    {
        (InMemoryVesselDb store, User user, Team personal) = UserWithPersonalTeam();
        UserSessionService sessions = Sessions(store);
        UserSessionId id = await sessions.StartAsync(user.Id, personal.Id, "password", SessionClient.Unknown, Ct);
        var token = PersonalAccessToken.Create(user.Id, personal.Id, "ci", "hash", "read", null, DateTimeOffset.UtcNow);
        store.Set<PersonalAccessToken>().Add(token);
        user.StartPasswordReset(TokenHashing.Sha256("reset"), DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow);

        Assert.True(await Authentication(store, sessions).ResetPasswordAsync(user.Email.Value, "reset",
            "Correct-Horse-Battery-9", null, Ct));

        Assert.False((await sessions.ValidateAsync(id, user.Id, Ct)).IsActive);
        Assert.True(token.IsRevoked);
    }

    // ---------- IAM-04 token expiry warning ----------

    [Fact]
    public void TokenExpiryWarning_IsDueSevenDaysAhead_OrOneDayForShortLivedTokens()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PersonalAccessToken longLived = PersonalAccessToken.Create(UserId.New(), TeamId.New(), "a", "h1", "read",
            now.AddDays(6), now.AddDays(-30));
        PersonalAccessToken shortLived = PersonalAccessToken.Create(UserId.New(), TeamId.New(), "b", "h2", "read",
            now.AddDays(3), now.AddDays(-1));
        PersonalAccessToken shortLivedLastDay = PersonalAccessToken.Create(UserId.New(), TeamId.New(), "c", "h3",
            "read", now.AddHours(12), now.AddDays(-2));

        Assert.True(longLived.IsExpirationWarningDue(now));
        Assert.False(shortLived.IsExpirationWarningDue(now));
        Assert.True(shortLivedLastDay.IsExpirationWarningDue(now));

        longLived.MarkExpirationWarningSent(now);
        Assert.False(longLived.IsExpirationWarningDue(now));
        longLived.Update("a", "read", null, now.AddDays(5), now);
        Assert.True(longLived.IsExpirationWarningDue(now));
    }

    [Fact]
    public async Task TokenExpiryWarningService_WarnsOncePerToken_AndSkipsFormerMembers()
    {
        (InMemoryVesselDb store, User user, Team personal) = UserWithPersonalTeam();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var member = PersonalAccessToken.Create(user.Id, personal.Id, "ci", "h1", "read", now.AddDays(5),
            now.AddDays(-30));
        var formerMember = PersonalAccessToken.Create(UserId.New(), personal.Id, "old", "h2", "read", now.AddDays(5),
            now.AddDays(-30));
        store.Set<PersonalAccessToken>().AddRange([member, formerMember]);
        var publisher = new RecordingPublisher();
        var service = new TokenExpiryWarningService(store.Context, publisher, new RecordingAudit(), TimeProvider.System);

        Assert.Equal(1, await service.RunAsync(Ct));
        Assert.Equal(0, await service.RunAsync(Ct));
        (TeamId team, RaiseNotificationRequest request) = Assert.Single(publisher.Published);
        Assert.Equal(personal.Id, team);
        Assert.Equal(NotificationEventTypes.ApiTokenExpiring, request.EventType);
        Assert.DoesNotContain("h1", request.Message, StringComparison.Ordinal);
    }

    // ---------- IAM-07 external sign-in ----------

    [Fact]
    public async Task ExternalLogin_LinksExistingAccount_OnlyWithAVerifiedEmail()
    {
        (InMemoryVesselDb store, User user, Team personal) = UserWithPersonalTeam();
        ExternalFixture fixture = External(store, AuthProviderKind.Oidc, allowRegistration: false,
            requireVerifiedEmail: false);

        fixture.Client.Next = Identity("sub-1", user.Email.Value, verified: false);
        ExternalLoginResult denied = await fixture.CompleteAsync(Ct);
        Assert.Equal(ExternalLoginStatus.Denied, denied.Status);
        Assert.Equal(ExternalLoginDenials.EmailUnverified, denied.DenialReason);
        Assert.Empty(store.Set<OauthIdentity>());

        fixture.Client.Next = Identity("sub-1", user.Email.Value, verified: true);
        ExternalLoginResult signedIn = await fixture.CompleteAsync(Ct);
        Assert.Equal(ExternalLoginStatus.SignedIn, signedIn.Status);
        Assert.Equal(user.Id, signedIn.User!.UserId);
        Assert.Equal(personal.Id, signedIn.User.TeamId);
        OauthIdentity identity = Assert.Single(store.Set<OauthIdentity>());
        Assert.Equal("sub-1", identity.Subject);

        // A second identity (other subject) cannot be linked to the same account.
        fixture.Client.Next = Identity("sub-2", user.Email.Value, verified: true);
        Assert.Equal(ExternalLoginDenials.AlreadyLinked, (await fixture.CompleteAsync(Ct)).DenialReason);

        // The linked subject signs in by subject even when the provider reports another address.
        fixture.Client.Next = Identity("sub-1", "renamed@example.com", verified: true);
        Assert.Equal(user.Id, (await fixture.CompleteAsync(Ct)).User!.UserId);

        // An issuer change is refused instead of silently re-linking.
        fixture.Client.Next = Identity("sub-1", user.Email.Value, verified: true) with { Issuer = "https://evil.example" };
        Assert.Equal(ExternalLoginDenials.IssuerMismatch, (await fixture.CompleteAsync(Ct)).DenialReason);
    }

    [Fact]
    public async Task ExternalLogin_NewAccounts_FollowTheRegistrationPolicy()
    {
        (InMemoryVesselDb store, _, _) = UserWithPersonalTeam();
        var autoJoin = Team.Create(new DisplayName("Everyone"), UserId.New(), false, DateTimeOffset.UtcNow);
        store.Set<Team>().Add(autoJoin);

        ExternalFixture closed = External(store, AuthProviderKind.Oidc, allowRegistration: false);
        closed.Client.Next = Identity("new-1", "new@example.com", verified: true);
        Assert.Equal(ExternalLoginDenials.RegistrationDisabled, (await closed.CompleteAsync(Ct)).DenialReason);
        Assert.DoesNotContain(store.Set<User>(), candidate => candidate.Email.Value == "new@example.com");

        ExternalFixture open = External(store, AuthProviderKind.Oidc, allowRegistration: true, autoJoin: autoJoin.Id);
        open.Client.Next = Identity("new-1", "new@example.com", verified: true);
        ExternalLoginResult created = await open.CompleteAsync(Ct);
        Assert.Equal(ExternalLoginStatus.SignedIn, created.Status);
        User newUser = store.Set<User>().Single(candidate => candidate.Email.Value == "new@example.com");
        Assert.False(newUser.IsInstanceAdmin);
        Assert.False(newUser.HasPassword);
        Assert.Equal(TeamRole.Member, autoJoin.GetMemberRole(newUser.Id));
    }

    [Fact]
    public async Task ExternalLogin_InvitedAddress_MayRegisterWhileRegistrationIsClosed()
    {
        (InMemoryVesselDb store, _, Team personal) = UserWithPersonalTeam();
        store.Set<TeamInvitation>().Add(TeamInvitation.Create(personal.Id, new EmailAddress("invited@example.com"),
            TeamRole.Member, "hash", DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow));
        ExternalFixture fixture = External(store, AuthProviderKind.Oidc, allowRegistration: false);
        fixture.Client.Next = Identity("inv", "invited@example.com", verified: true);

        ExternalLoginResult result = await fixture.CompleteAsync(Ct);

        Assert.Equal(ExternalLoginStatus.SignedIn, result.Status);
        // The invitation is not accepted implicitly: the new user lands in their own personal team.
        Assert.NotEqual(personal.Id, result.User!.TeamId);
    }

    [Fact]
    public async Task ExternalLogin_TwoFactorAccounts_GetAChallengeInsteadOfASession()
    {
        (InMemoryVesselDb store, User user, _) = UserWithPersonalTeam();
        var cipher = new FakeSecretCipher("v1");
        user.StageTwoFactorSecret(new TwoFactorSecretProtector(cipher).Protect(user.Id, TestTotp.Secret),
            DateTimeOffset.UtcNow);
        user.ConfirmTwoFactor("hash", DateTimeOffset.UtcNow);
        ExternalFixture fixture = External(store, AuthProviderKind.Oidc, allowRegistration: false);
        fixture.Client.Next = Identity("sub-2fa", user.Email.Value, verified: true);

        ExternalLoginResult result = await fixture.CompleteAsync(Ct);

        Assert.Equal(ExternalLoginStatus.TwoFactorRequired, result.Status);
        Assert.Null(result.User);
        Assert.Equal(user.Id, result.PendingUserId);
        Assert.Null(await Authentication(store).CompleteExternalTwoFactorAsync(user.Id, result.AuthenticationMethod,
            "000000", null, Ct));
    }

    [Fact]
    public async Task ExternalLogin_GoogleWorkspaceRestriction_RequiresTheHostedDomain()
    {
        (InMemoryVesselDb store, User user, _) = UserWithPersonalTeam();
        ExternalFixture fixture = External(store, AuthProviderKind.Google, allowRegistration: false,
            allowedDomains: "example.com");

        fixture.Client.Next = Identity("g-1", user.Email.Value, verified: true);
        Assert.Equal(ExternalLoginDenials.DomainNotAllowed, (await fixture.CompleteAsync(Ct)).DenialReason);

        fixture.Client.Next = Identity("g-1", user.Email.Value, verified: true) with { HostedDomain = "other.com" };
        Assert.Equal(ExternalLoginDenials.DomainNotAllowed, (await fixture.CompleteAsync(Ct)).DenialReason);

        fixture.Client.Next = Identity("g-1", user.Email.Value, verified: true) with { HostedDomain = "example.com" };
        Assert.Equal(ExternalLoginStatus.SignedIn, (await fixture.CompleteAsync(Ct)).Status);
    }

    [Fact]
    public async Task ExternalLogin_CallbackUrlComesFromTheInstanceUrl_AndPkceIsUsed()
    {
        (InMemoryVesselDb store, _, _) = UserWithPersonalTeam();
        ExternalFixture fixture = External(store, AuthProviderKind.Oidc, allowRegistration: false);

        ExternalLoginChallenge challenge = await fixture.Service.BeginAsync(fixture.Provider.Id, "/", Ct);

        var query = challenge.AuthorizationUrl.Query;
        Assert.Contains(Uri.EscapeDataString($"https://vessel.example.com/auth/oauth/{fixture.Provider.Id.Value:D}/callback"),
            query, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", query, StringComparison.Ordinal);
        Assert.Contains("nonce=", query, StringComparison.Ordinal);

        ExternalFixture noUrl = External(store, AuthProviderKind.Oidc, allowRegistration: false, publicUrl: null);
        ExternalLoginException exception = await Assert.ThrowsAsync<ExternalLoginException>(() =>
            noUrl.Service.BeginAsync(noUrl.Provider.Id, "/", Ct));
        Assert.Equal(ExternalLoginDenials.InstanceUrlMissing, exception.Reason);
    }

    [Fact]
    public async Task AuthProviderAdmin_RequiresInstanceAdmin_AndNeverReturnsTheSecret()
    {
        (InMemoryVesselDb store, User user, _) = UserWithPersonalTeam();
        var cipher = new FakeSecretCipher("v1");
        ExternalFixture fixture = External(store, AuthProviderKind.GitHub, allowRegistration: false);
        var admin = new AuthProviderAdminService(store.Context, new VesselAuthorizationService(store.Context), cipher,
            new AllowAllUrls(), fixture.Service, new RecordingAudit(), TimeProvider.System);
        var input = new AuthProviderInput(AuthProviderKind.GitHub, "GitHub", "client", "super-secret", null, null,
            true, false, null, null);

        Assert.Throws<UnauthorizedAccessException>(() => admin.List(user.Id));
        user.GrantInstanceAdmin(DateTimeOffset.UtcNow);
        AuthProviderSummary created = await admin.CreateAsync(user.Id, input, null, Ct);

        Assert.True(created.ClientSecretSet);
        Assert.DoesNotContain("super-secret", created.ToString(), StringComparison.Ordinal);
        AuthProvider stored = store.Set<AuthProvider>().Single(provider => provider.Id == created.Id);
        Assert.NotEqual("super-secret", stored.ProtectedClientSecret);
        Assert.Equal("super-secret", cipher.Unprotect(stored.ProtectedClientSecret!,
            ExternalLoginService.ClientSecretPurpose));
    }

    // ---------- helpers ----------

    private static AuditLog Audit(TeamId teamId, string action)
    {
        return AuditLog.Record(teamId, null, action, new AuditTarget("x", Guid.NewGuid().ToString()), null, "{}",
            DateTimeOffset.UtcNow);
    }

    private static (InMemoryVesselDb Store, User User, Team Personal) UserWithPersonalTeam()
    {
        InMemoryVesselDb store = InMemoryVesselDb.Create();
        var user = User.Create(new DisplayName("Jane"), new EmailAddress("jane@example.com"), DateTimeOffset.UtcNow);
        user.SetPasswordHash(new FakePasswordHasher().HashPassword("pw"), false, DateTimeOffset.UtcNow);
        var personal = Team.Create(new DisplayName("Jane's Team"), user.Id, true, DateTimeOffset.UtcNow);
        store.Set<User>().Add(user);
        store.Set<Team>().Add(personal);
        return (store, user, personal);
    }

    private static UserSessionService Sessions(InMemoryVesselDb store)
    {
        return new UserSessionService(store.Context, new RecordingAudit(), Options.Create(new AuthOptions()),
            TimeProvider.System);
    }

    private static VesselAuthenticationService Authentication(InMemoryVesselDb store, UserSessionService? sessions = null)
    {
        var tokens = new SequentialTokenGenerator();
        return new VesselAuthenticationService(store.Context, new FakePasswordHasher(), tokens, new TotpService(tokens),
            new RecordingAudit(), Options.Create(new AuthOptions()), new FakeSecretCipher("v1"), null,
            sessions ?? Sessions(store));
    }

    private static ExternalUserInfo Identity(string subject, string email, bool verified)
    {
        return new ExternalUserInfo("https://idp.example.com", subject, email, verified, "Someone", null);
    }

    private static ExternalFixture External(InMemoryVesselDb store, AuthProviderKind kind, bool allowRegistration,
        TeamId? autoJoin = null, string? allowedDomains = null, bool requireVerifiedEmail = true,
        string? publicUrl = "https://vessel.example.com")
    {
        var cipher = new FakeSecretCipher("v1");
        AuthProvider provider = AuthProvider.Create(kind, "SSO", "client-id",
            cipher.Protect("secret", ExternalLoginService.ClientSecretPurpose),
            kind == AuthProviderKind.Oidc ? "https://idp.example.com" : null, null, DateTimeOffset.UtcNow);
        provider.SetRegistrationPolicy(allowRegistration, autoJoin, allowedDomains, requireVerifiedEmail,
            DateTimeOffset.UtcNow);
        provider.Enable(DateTimeOffset.UtcNow);
        store.Set<AuthProvider>().Add(provider);

        var client = new FakeOAuthClient();
        var audit = new RecordingAudit();
        var authorization = new VesselAuthorizationService(store.Context);
        var tokens = new SequentialTokenGenerator();
        var service = new ExternalLoginService(store.Context, client, cipher,
            new InstanceLinkBuilder(Options.Create(new InstanceUrlOptions { PublicUrl = publicUrl })),
            new InstanceSettingsService(store.Context, authorization, audit, TimeProvider.System),
            Authentication(store), audit, tokens, TimeProvider.System);
        return new ExternalFixture(service, client, provider);
    }

    private sealed record ExternalFixture(ExternalLoginService Service, FakeOAuthClient Client, AuthProvider Provider)
    {
        public Task<ExternalLoginResult> CompleteAsync(CancellationToken cancellationToken)
        {
            return Service.CompleteAsync(new ExternalLoginChallenge(Provider.Id, new Uri("https://idp.example.com"),
                "state", "nonce", "verifier", "/"), "code", null, cancellationToken);
        }
    }

    private sealed class FakeOAuthClient : IOAuthProviderClient
    {
        public ExternalUserInfo? Next { get; set; }

        public Task<OAuthEndpoints> ResolveEndpointsAsync(AuthProviderKind kind, string authority,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OAuthEndpoints(authority, new Uri(authority + "/authorize"),
                new Uri(authority + "/token"), null, kind != AuthProviderKind.GitHub));
        }

        public Task<ExternalUserInfo> ExchangeCodeAsync(OAuthExchangeRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("secret", request.ClientSecret);
            return Task.FromResult(Next ?? throw new InvalidOperationException("No identity configured."));
        }
    }

    private sealed class AllowAllUrls : IOutboundUrlPolicy
    {
        public Task<OutboundTargetCheck> CheckUrlAsync(Uri url, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(OutboundTargetCheck.Allow());
        }

        public Task<OutboundTargetCheck> CheckHostAsync(string host, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(OutboundTargetCheck.Allow());
        }

        public bool IsAddressAllowed(string host, System.Net.IPAddress address)
        {
            return true;
        }
    }

    private sealed class FixedTokenContext(ApiTokenAccess access) : IApiTokenAccessContext
    {
        public ApiTokenAccess? Current => access;
    }

    internal sealed class RecordingAudit : IAuditWriter
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task RecordAsync(TeamId? teamId, UserId? actorUserId, string action, AuditTarget target,
            string? correlationId, IReadOnlyDictionary<string, object?> metadata,
            CancellationToken cancellationToken = default)
        {
            Entries.Add(new AuditEntry(teamId, actorUserId, action, target) { Metadata = metadata });
            return Task.CompletedTask;
        }

        public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task StageAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
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
            if (build() is { } notification) Published.Add((notification.TeamId, notification.Notification));
            return Task.CompletedTask;
        }
    }
}
