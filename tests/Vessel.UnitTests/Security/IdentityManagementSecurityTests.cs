using Microsoft.Extensions.Options;
using Vessel.Application.Auditing;
using Vessel.Application.Auth;
using Vessel.Application.Authorization;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;

namespace Vessel.UnitTests.Security;

/// <summary>IAM-01 regression tests: token escalation, membership re-validation and 2FA re-confirmation.</summary>
public sealed class IdentityManagementSecurityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(TokenScopeMapper.Root)]
    [InlineData(TokenScopeMapper.Write)]
    [InlineData(TokenScopeMapper.WriteSensitive)]
    [InlineData(TokenScopeMapper.ReadSensitive)]
    public async Task Member_CannotMintTokenAboveTheirRole(string scope)
    {
        var fixture = CrossTeamFixture.Create();
        VesselTokenService tokens = CreateTokenService(fixture);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tokens.CreateAsync(
            fixture.Actor.Id, fixture.TeamB.Id, "escalate", [scope], null, null, null, Ct));

        Assert.Empty(fixture.Store.Set<PersonalAccessToken>());
    }

    [Fact]
    public async Task Member_CanMintTokensWithinTheirRole()
    {
        var fixture = CrossTeamFixture.Create();
        VesselTokenService tokens = CreateTokenService(fixture);

        await tokens.CreateAsync(fixture.Actor.Id, fixture.TeamB.Id, "ci", [TokenScopeMapper.Read,
            TokenScopeMapper.Deploy], null, null, null, Ct);

        Assert.Single(fixture.Store.Set<PersonalAccessToken>());
    }

    [Fact]
    public async Task NonMember_CannotMintTokenForTeam()
    {
        var fixture = CrossTeamFixture.Create();
        VesselTokenService tokens = CreateTokenService(fixture);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tokens.CreateAsync(
            fixture.OwnerOfB.Id, fixture.TeamA.Id, "x", [TokenScopeMapper.Read], null, null, null, Ct));
    }

    [Theory]
    [InlineData(TokenScopeMapper.Read)]
    [InlineData(TokenScopeMapper.Write)]
    [InlineData(TokenScopeMapper.Deploy)]
    public async Task PresentingNonRootToken_CannotMintAnyToken(string presentingScope)
    {
        var fixture = CrossTeamFixture.Create();
        VesselTokenService tokens = CreateTokenService(fixture);

        // Even an admin's read/write token cannot mint a root token (or any token at all).
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tokens.CreateAsync(
            fixture.Actor.Id, fixture.TeamA.Id, "escalate", [TokenScopeMapper.Root], null,
            new HashSet<string>(StringComparer.Ordinal) { presentingScope }, null, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tokens.CreateAsync(
            fixture.Actor.Id, fixture.TeamA.Id, "same", [presentingScope], null,
            new HashSet<string>(StringComparer.Ordinal) { presentingScope }, null, Ct));

        Assert.Empty(fixture.Store.Set<PersonalAccessToken>());
    }

    [Fact]
    public async Task Admin_WithRootTokenOrSession_CanMintRootToken()
    {
        var fixture = CrossTeamFixture.Create();
        VesselTokenService tokens = CreateTokenService(fixture);

        await tokens.CreateAsync(fixture.Actor.Id, fixture.TeamA.Id, "session", [TokenScopeMapper.Root], null, null,
            null, Ct);
        await tokens.CreateAsync(fixture.Actor.Id, fixture.TeamA.Id, "via-root", [TokenScopeMapper.Root], null,
            new HashSet<string>(StringComparer.Ordinal) { TokenScopeMapper.Root }, null, Ct);

        Assert.Equal(2, fixture.Store.Set<PersonalAccessToken>().Count);
    }

    [Fact]
    public async Task CreateToken_RejectsExpiryInThePast()
    {
        var fixture = CrossTeamFixture.Create();
        VesselTokenService tokens = CreateTokenService(fixture);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tokens.CreateAsync(fixture.Actor.Id,
            fixture.TeamA.Id, "expired", [TokenScopeMapper.Read], DateTimeOffset.UtcNow.AddMinutes(-1), null, null,
            Ct));
    }

    [Fact]
    public async Task ValidateToken_FailsWhenOwnerIsNoLongerTeamMember()
    {
        var fixture = CrossTeamFixture.Create();
        VesselTokenService tokens = CreateTokenService(fixture);
        CreatedToken created = await tokens.CreateAsync(fixture.Actor.Id, fixture.TeamB.Id, "ci",
            [TokenScopeMapper.Read], null, null, null, Ct);
        Assert.True((await tokens.ValidateAsync(created.PlainTextToken, Ct)).Succeeded);

        // Remove the membership directly (bypassing the revocation in VesselTeamService) to prove that
        // validation itself re-checks membership on every request.
        fixture.TeamB.RemoveMember(fixture.Actor.Id, DateTimeOffset.UtcNow);

        AuthTokenValidationResult result = await tokens.ValidateAsync(created.PlainTextToken, Ct);
        Assert.False(result.Succeeded);
        Assert.Empty(result.Permissions);
    }

    [Fact]
    public async Task ValidateToken_ClampsPermissionsToOwnersCurrentRole()
    {
        var fixture = CrossTeamFixture.Create();
        VesselTokenService tokens = CreateTokenService(fixture);
        CreatedToken created = await tokens.CreateAsync(fixture.Actor.Id, fixture.TeamA.Id, "root",
            [TokenScopeMapper.Root], null, null, null, Ct);

        fixture.TeamA.ChangeMemberRole(fixture.Actor.Id, TeamRole.Member, DateTimeOffset.UtcNow);
        AuthTokenValidationResult result = await tokens.ValidateAsync(created.PlainTextToken, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(VesselPermissions.ForRole(TeamRole.Member).Order(),
            result.Permissions.Order());
        Assert.DoesNotContain(VesselPermissions.SecretsRead, result.Permissions);
        Assert.DoesNotContain(VesselPermissions.TeamsManage, result.Permissions);
    }

    [Fact]
    public async Task ValidateToken_FailsWhenRevokedOrExpired()
    {
        var fixture = CrossTeamFixture.Create();
        VesselTokenService tokens = CreateTokenService(fixture);
        CreatedToken revoked = await tokens.CreateAsync(fixture.Actor.Id, fixture.TeamA.Id, "revoked",
            [TokenScopeMapper.Read], null, null, null, Ct);
        await tokens.RevokeAsync(fixture.Actor.Id, revoked.Id, null, Ct);

        var expiredToken = PersonalAccessToken.Create(fixture.Actor.Id, fixture.TeamA.Id, "expired",
            TokenHashing.Sha256("secret"), TokenScopeMapper.Read, DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow.AddDays(-1));
        fixture.Store.Set<PersonalAccessToken>().Add(expiredToken);

        Assert.False((await tokens.ValidateAsync(revoked.PlainTextToken, Ct)).Succeeded);
        Assert.False((await tokens.ValidateAsync($"{expiredToken.Id.Value:N}|secret", Ct)).Succeeded);
    }

    [Fact]
    public async Task RemoveMember_RevokesTheirTokensForThatTeamOnly()
    {
        var fixture = CrossTeamFixture.Create();
        VesselTokenService tokens = CreateTokenService(fixture);
        CreatedToken teamBToken = await tokens.CreateAsync(fixture.Actor.Id, fixture.TeamB.Id, "b",
            [TokenScopeMapper.Read], null, null, null, Ct);
        CreatedToken teamAToken = await tokens.CreateAsync(fixture.Actor.Id, fixture.TeamA.Id, "a",
            [TokenScopeMapper.Read], null, null, null, Ct);
        var teams = new VesselTeamService(fixture.Store.Context, new SequentialTokenGenerator(), new NullAuditWriter());

        await teams.RemoveMemberAsync(fixture.OwnerOfB.Id, fixture.TeamB.Id, fixture.Actor.Id, null, Ct);

        PersonalAccessToken[] stored = [.. fixture.Store.Set<PersonalAccessToken>()];
        Assert.True(stored.Single(token => token.Id == teamBToken.Id).IsRevoked);
        Assert.False(stored.Single(token => token.Id == teamAToken.Id).IsRevoked);
        Assert.True((await tokens.ValidateAsync(teamAToken.PlainTextToken, Ct)).Succeeded);
    }

    [Fact]
    public async Task DisableTwoFactor_WithoutReconfirmation_IsRejected()
    {
        var fixture = CrossTeamFixture.Create();
        User user = EnableTwoFactor(fixture);
        VesselAuthenticationService authentication = CreateAuthenticationService(fixture);

        Assert.False(await authentication.DisableTwoFactorAsync(user.Id, null, null, null, Ct));
        Assert.False(await authentication.DisableTwoFactorAsync(user.Id, "wrong-password", "000000", null, Ct));

        Assert.NotNull(user.TwoFactorConfirmedAt);
        Assert.Equal(1, user.FailedLoginCount);
    }

    [Fact]
    public async Task DisableTwoFactor_WithPasswordOrCurrentCode_Succeeds()
    {
        var fixture = CrossTeamFixture.Create();
        User user = EnableTwoFactor(fixture);
        VesselAuthenticationService authentication = CreateAuthenticationService(fixture);

        Assert.True(await authentication.DisableTwoFactorAsync(user.Id, null,
            TestTotp.CurrentCode(DateTimeOffset.UtcNow), null, Ct));
        Assert.Null(user.TwoFactorConfirmedAt);

        EnableTwoFactor(fixture);
        Assert.True(await authentication.DisableTwoFactorAsync(user.Id, "correct-password", null, null, Ct));
        Assert.Null(user.TwoFactorConfirmedAt);
        Assert.Null(user.TwoFactorSecret);
    }

    [Fact]
    public async Task StartTwoFactorSetup_WhenAlreadyEnabled_DoesNotReplaceTheActiveFactor()
    {
        var fixture = CrossTeamFixture.Create();
        User user = EnableTwoFactor(fixture);
        VesselAuthenticationService authentication = CreateAuthenticationService(fixture);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authentication.StartTwoFactorSetupAsync(user.Id, "Vessel", Ct));

        Assert.NotNull(user.TwoFactorConfirmedAt);
        Assert.Equal(TestTotp.Secret, user.TwoFactorSecret);
    }

    private static User EnableTwoFactor(CrossTeamFixture fixture)
    {
        User user = fixture.Actor;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        user.SetPasswordHash(new FakePasswordHasher().HashPassword("correct-password"), false, now);
        user.StageTwoFactorSecret(TestTotp.Secret, now);
        user.ConfirmTwoFactor("recovery-hash", now);
        return user;
    }

    private static VesselTokenService CreateTokenService(CrossTeamFixture fixture)
    {
        return new VesselTokenService(fixture.Store.Context, new SequentialTokenGenerator(), new NullAuditWriter());
    }

    private static VesselAuthenticationService CreateAuthenticationService(CrossTeamFixture fixture)
    {
        var tokenGenerator = new SequentialTokenGenerator();
        return new VesselAuthenticationService(fixture.Store.Context, new FakePasswordHasher(), tokenGenerator,
            new TotpService(tokenGenerator), new NullAuditWriter(), Options.Create(new AuthOptions()));
    }

    private sealed class NullAuditWriter : IAuditWriter
    {
        public Task RecordAsync(TeamId? teamId, UserId? actorUserId, string action, AuditTarget target,
            string? correlationId, IReadOnlyDictionary<string, object?> metadata,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
