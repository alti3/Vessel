using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;
using Vessel.Web.Controllers;
using Vessel.Web.Security;

namespace Vessel.E2ETests.Security;

/// <summary>
///     IAM-01 Web-boundary tests: bearer tokens cannot reach identity-management endpoints, cannot fall back to
///     their owner's role, and team switching never copies token claims into a cookie session.
/// </summary>
public sealed class IdentityEndpointSecurityTests
{
    [Fact]
    public void TokenEndpoints_RequireInteractiveSessionOrRootToken()
    {
        AssertPolicy(typeof(TokensController), VesselAuthorizationPolicies.InteractiveSessionOrRootToken);
    }

    [Theory]
    [InlineData(nameof(TeamsController.Switch))]
    [InlineData(nameof(TeamsController.Invite))]
    [InlineData(nameof(TeamsController.AcceptInvitation))]
    [InlineData(nameof(TeamsController.ChangeRole))]
    [InlineData(nameof(TeamsController.RemoveMember))]
    public void TeamMutationEndpoints_RequireInteractiveSession(string action)
    {
        AssertPolicy(typeof(TeamsController).GetMethod(action)!, VesselAuthorizationPolicies.InteractiveSession);
    }

    [Theory]
    [InlineData(nameof(AuthController.StartTwoFactorSetup))]
    [InlineData(nameof(AuthController.ConfirmTwoFactor))]
    [InlineData(nameof(AuthController.DisableTwoFactor))]
    public void TwoFactorEndpoints_RequireInteractiveSession(string action)
    {
        AssertPolicy(typeof(AuthController).GetMethod(action)!, VesselAuthorizationPolicies.InteractiveSession);
    }

    [Fact]
    public void BearerTokens_AreNeverInteractiveSessions()
    {
        ClaimsPrincipal readToken = Bearer(Guid.NewGuid(), Guid.NewGuid(), [TokenScopeMapper.Read], []);
        ClaimsPrincipal rootToken = Bearer(Guid.NewGuid(), Guid.NewGuid(), [TokenScopeMapper.Root], []);
        ClaimsPrincipal session = Session(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(VesselAuthorizationPolicies.IsInteractiveSession(readToken));
        Assert.False(VesselAuthorizationPolicies.IsInteractiveSession(rootToken));
        Assert.True(VesselAuthorizationPolicies.IsInteractiveSession(session));

        Assert.False(VesselAuthorizationPolicies.IsInteractiveSessionOrRootToken(readToken));
        Assert.True(VesselAuthorizationPolicies.IsInteractiveSessionOrRootToken(rootToken));
        Assert.True(VesselAuthorizationPolicies.IsInteractiveSessionOrRootToken(session));
    }

    [Fact]
    public void CookieSessionCarryingTokenClaims_IsNotTrustedAsInteractive()
    {
        // A legacy session produced by the old team-switch (token claims copied into a cookie).
        var identity = new ClaimsIdentity(Session(Guid.NewGuid(), Guid.NewGuid()).Claims, "Vessel.Cookie");
        identity.AddClaim(new Claim(VesselClaimTypes.TokenId, Guid.NewGuid().ToString("D")));
        identity.AddClaim(new Claim(VesselClaimTypes.Scope, TokenScopeMapper.Root));
        identity.AddClaim(new Claim(VesselClaimTypes.Permission, VesselPermissions.SecretsWrite));
        var legacy = new ClaimsPrincipal(identity);

        Assert.False(VesselAuthorizationPolicies.IsInteractiveSession(legacy));
        Assert.False(VesselAuthorizationPolicies.IsInteractiveSessionOrRootToken(legacy));
    }

    [Fact]
    public void TeamSwitch_ReissuesSessionWithoutTokenClaims()
    {
        var identity = new ClaimsIdentity(Session(Guid.NewGuid(), Guid.NewGuid()).Claims, "Vessel.Cookie");
        identity.AddClaim(new Claim(VesselClaimTypes.TokenId, Guid.NewGuid().ToString("D")));
        identity.AddClaim(new Claim(VesselClaimTypes.Permission, VesselPermissions.SecretsWrite));
        var newTeam = new TeamId(Guid.NewGuid());

        ClaimsPrincipal switched = VesselSessionPrincipal.WithTeam(new ClaimsPrincipal(identity), newTeam);

        Assert.Equal(newTeam, switched.GetTeamId());
        Assert.False(switched.HasClaim(claim => claim.Type == VesselClaimTypes.TokenId));
        Assert.False(switched.HasClaim(claim => claim.Type == VesselClaimTypes.Permission));
        Assert.False(switched.HasClaim(claim => claim.Type == VesselClaimTypes.Scope));
        Assert.True(VesselAuthorizationPolicies.IsInteractiveSession(switched));
    }

    [Fact]
    public async Task BearerToken_WithoutPermissionClaim_DoesNotFallBackToOwnersRole()
    {
        (VesselAuthorizationService authorization, Guid userId, Guid teamId) = OwnerOfTeam();
        var handler = new PermissionAuthorizationHandler(authorization);

        // The owner holds every permission, but this token only carries the read permissions.
        ClaimsPrincipal readToken = Bearer(userId, teamId, [TokenScopeMapper.Read],
            TokenScopeMapper.ToPermissions([TokenScopeMapper.Read]));

        Assert.False(await SucceedsAsync(handler, readToken, VesselPermissions.ProjectsWrite));
        Assert.False(await SucceedsAsync(handler, readToken, VesselPermissions.SecretsRead));
        Assert.True(await SucceedsAsync(handler, readToken, VesselPermissions.ProjectsRead));
    }

    [Fact]
    public async Task BearerToken_PermissionClaimIsStillBoundedByOwnersCurrentRole()
    {
        (VesselAuthorizationService authorization, Guid _, Guid teamId) = OwnerOfTeam();
        var handler = new PermissionAuthorizationHandler(authorization);

        // A forged/stale principal for a user who is not a member of the token's team.
        ClaimsPrincipal stranger = Bearer(Guid.NewGuid(), teamId, [TokenScopeMapper.Root], VesselPermissions.All);

        Assert.False(await SucceedsAsync(handler, stranger, VesselPermissions.ProjectsRead));
    }

    [Fact]
    public async Task CookieSession_IgnoresInjectedPermissionClaims()
    {
        (VesselAuthorizationService authorization, Guid userId, Guid teamId) = OwnerOfTeam();
        var handler = new PermissionAuthorizationHandler(authorization);
        var identity = new ClaimsIdentity(Session(Guid.NewGuid(), teamId).Claims, "Vessel.Cookie");
        identity.AddClaim(new Claim(VesselClaimTypes.Permission, VesselPermissions.SecretsWrite));

        Assert.False(await SucceedsAsync(handler, new ClaimsPrincipal(identity), VesselPermissions.SecretsWrite));
        Assert.True(await SucceedsAsync(handler, Session(userId, teamId), VesselPermissions.SecretsWrite));
    }

    private static async Task<bool> SucceedsAsync(
        PermissionAuthorizationHandler handler,
        ClaimsPrincipal principal,
        string permission)
    {
        var context = new AuthorizationHandlerContext([new PermissionRequirement(permission)], principal, null);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static (VesselAuthorizationService, Guid UserId, Guid TeamId) OwnerOfTeam()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var owner = User.Create(new DisplayName("Owner"), new EmailAddress("owner@example.com"), now);
        var team = Team.Create(new DisplayName("Team"), owner.Id, false, now);
        var db = DispatchProxyDb.Create([team]);
        return (new VesselAuthorizationService(db), owner.Id.Value, team.Id.Value);
    }

    private static ClaimsPrincipal Session(Guid userId, Guid teamId)
    {
        return VesselSessionPrincipal.Create(new UserId(userId), "User", "user@example.com", new TeamId(teamId));
    }

    private static ClaimsPrincipal Bearer(
        Guid userId,
        Guid teamId,
        IEnumerable<string> scopes,
        IEnumerable<string> permissions)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, userId.ToString("D")),
            new(VesselClaimTypes.TeamId, teamId.ToString("D")),
            new(VesselClaimTypes.TokenId, Guid.NewGuid().ToString("D"))
        ];
        claims.AddRange(scopes.Select(scope => new Claim(VesselClaimTypes.Scope, scope)));
        claims.AddRange(permissions.Select(permission => new Claim(VesselClaimTypes.Permission, permission)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Vessel.Bearer"));
    }

    private static void AssertPolicy(MemberInfo member, string policy)
    {
        Assert.Contains(member.GetCustomAttributes<AuthorizeAttribute>(true), attribute => attribute.Policy == policy);
    }

    /// <summary>Minimal <see cref="IVesselDbContext" /> exposing team memberships; every other set is empty.</summary>
    public class DispatchProxyDb : DispatchProxy
    {
        private IReadOnlyList<Team> _teams = [];

        public static IVesselDbContext Create(IReadOnlyList<Team> teams)
        {
            IVesselDbContext db = DispatchProxy.Create<IVesselDbContext, DispatchProxyDb>();
            ((DispatchProxyDb)(object)db)._teams = teams;
            return db;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == "get_" + nameof(IVesselDbContext.TeamMemberships))
                return _teams.SelectMany(team => team.Memberships).AsQueryable();

            Type returnType = targetMethod.ReturnType;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(IQueryable<>))
            {
                Type entityType = returnType.GetGenericArguments()[0];
                return Queryable.AsQueryable((System.Collections.IEnumerable)Array.CreateInstance(entityType, 0));
            }

            throw new NotSupportedException(targetMethod.Name);
        }
    }
}
