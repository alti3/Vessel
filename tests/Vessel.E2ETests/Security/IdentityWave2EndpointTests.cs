using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Vessel.Application.Authorization;
using Vessel.Domain;
using Vessel.Web.Controllers;
using Vessel.Web.Controllers.Api.V1;
using Vessel.Web.Security;

namespace Vessel.E2ETests.Security;

/// <summary>Web-boundary checks for IAM-07 (providers), IAM-08 (audit API) and IAM-09 (sessions).</summary>
public sealed class IdentityWave2EndpointTests
{
    [Theory]
    [InlineData(nameof(AuthController.Sessions))]
    [InlineData(nameof(AuthController.RevokeSession))]
    [InlineData(nameof(AuthController.RevokeOtherSessions))]
    public void SessionEndpoints_RequireAnInteractiveSession(string action)
    {
        Assert.Contains(typeof(AuthController).GetMethod(action)!.GetCustomAttributes<AuthorizeAttribute>(true),
            attribute => attribute.Policy == VesselAuthorizationPolicies.InteractiveSession);
    }

    [Fact]
    public void AuthProviderAdministration_RequiresInstanceAdminAndSession()
    {
        var policies = typeof(AuthProvidersController).GetCustomAttributes<AuthorizeAttribute>(true)
            .Select(attribute => attribute.Policy).ToArray();
        Assert.Contains(VesselPermissions.InstanceManage, policies);
        Assert.Contains(VesselAuthorizationPolicies.InteractiveSession, policies);
    }

    [Fact]
    public void AuditEvents_RequireTheAuditPermission_WhichMembersDoNotHold()
    {
        Assert.Contains(typeof(AuditEventsController).GetCustomAttributes<AuthorizeAttribute>(true),
            attribute => attribute.Policy == VesselPermissions.AuditRead);
        Assert.DoesNotContain(VesselPermissions.AuditRead, VesselPermissions.ForRole(Vessel.Domain.Teams.TeamRole.Member));
        Assert.Contains(VesselPermissions.AuditRead, VesselPermissions.ForRole(Vessel.Domain.Teams.TeamRole.Admin));
    }

    [Fact]
    public void SessionPrincipal_CarriesTheSessionId_AcrossTeamSwitches()
    {
        var sessionId = UserSessionId.New();
        ClaimsPrincipal principal = VesselSessionPrincipal.Create(UserId.New(), "n", "e@example.com", TeamId.New(),
            sessionId);
        ClaimsPrincipal switched = VesselSessionPrincipal.WithTeam(principal, TeamId.New());

        Assert.Equal(sessionId, switched.GetSessionId());
        Assert.True(VesselAuthorizationPolicies.IsInteractiveSession(switched));
    }
}
