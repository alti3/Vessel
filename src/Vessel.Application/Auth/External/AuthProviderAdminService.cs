using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.OutboundRequests;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Auth;
using Vessel.Domain.Common;

namespace Vessel.Application.Auth.External;

/// <summary>Create/update input. <see cref="ClientSecret" /> <c>null</c> keeps the stored secret (write-only field).</summary>
public sealed record AuthProviderInput(
    AuthProviderKind Kind,
    string Name,
    string ClientId,
    string? ClientSecret,
    string? Authority,
    string? Scopes,
    bool Enabled,
    bool AllowRegistration,
    Guid? AutoJoinTeamId,
    string? AllowedDomains,
    bool RequireVerifiedEmail = true)
{
    public override string ToString()
    {
        return $"AuthProviderInput {{ Kind = {Kind}, Name = {Name}, ClientSecret = [redacted] }}";
    }
}

/// <summary>Provider settings as shown to administrators; the secret is reported only as set/unset.</summary>
public sealed record AuthProviderSummary(
    AuthProviderId Id,
    AuthProviderKind Kind,
    string Name,
    string ClientId,
    bool ClientSecretSet,
    string? Authority,
    string Scopes,
    bool Enabled,
    bool AllowRegistration,
    Guid? AutoJoinTeamId,
    string? AllowedDomains,
    bool RequireVerifiedEmail,
    string? CallbackUrl,
    int LinkedIdentities);

/// <summary>
///     Instance-administrator management of external sign-in providers (IAM-07). Client secrets are protected with the
///     key ring before they are stored and never returned or audited; provider URLs must pass the outbound URL policy
///     (SSRF guard) when saved.
/// </summary>
public sealed class AuthProviderAdminService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    ISecretCipher cipher,
    IOutboundUrlPolicy urlPolicy,
    ExternalLoginService externalLogin,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public IReadOnlyList<AuthProviderSummary> List(UserId actorUserId)
    {
        RequireInstanceManager(actorUserId);
        return dbContext.AuthProviders.OrderBy(provider => provider.Name).AsEnumerable().Select(ToSummary).ToArray();
    }

    public async Task<AuthProviderSummary> CreateAsync(UserId actorUserId, AuthProviderInput input,
        string? correlationId, CancellationToken cancellationToken = default)
    {
        RequireInstanceManager(actorUserId);
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.ClientSecret))
            throw new InvalidOperationException("A client secret is required.");
        DateTimeOffset now = timeProvider.GetUtcNow();
        AuthProvider provider;
        try
        {
            provider = AuthProvider.Create(input.Kind, input.Name, input.ClientId,
                cipher.Protect(input.ClientSecret.Trim(), ExternalLoginService.ClientSecretPurpose), input.Authority,
                input.Scopes, now);
            await ApplyPolicyAsync(provider, input, now, cancellationToken);
        }
        catch (DomainException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }

        await dbContext.AuthProviderRepository.AddAsync(provider, cancellationToken);
        await auditWriter.StageAsync(Audit(provider, actorUserId, AuditActions.AuthProviderCreated, correlationId,
            null), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToSummary(provider);
    }

    public async Task<AuthProviderSummary?> UpdateAsync(UserId actorUserId, AuthProviderId providerId,
        AuthProviderInput input, string? correlationId, CancellationToken cancellationToken = default)
    {
        RequireInstanceManager(actorUserId);
        ArgumentNullException.ThrowIfNull(input);
        AuthProvider? provider = await dbContext.AuthProviderRepository.GetByIdAsync(providerId, cancellationToken);
        if (provider is null) return null;
        if (provider.Kind != input.Kind) throw new InvalidOperationException("The provider kind cannot be changed.");

        Dictionary<string, object?> before = Snapshot(provider);
        DateTimeOffset now = timeProvider.GetUtcNow();
        try
        {
            provider.Configure(input.Name, input.ClientId, input.Authority, input.Scopes, now);
            if (!string.IsNullOrWhiteSpace(input.ClientSecret))
                provider.ReplaceClientSecret(
                    cipher.Protect(input.ClientSecret.Trim(), ExternalLoginService.ClientSecretPurpose), now);
            await ApplyPolicyAsync(provider, input, now, cancellationToken);
        }
        catch (DomainException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }

        Dictionary<string, object?> after = Snapshot(provider);
        if (!string.IsNullOrWhiteSpace(input.ClientSecret)) after["clientSecret"] = "rotated";
        await auditWriter.StageAsync(Audit(provider, actorUserId, AuditActions.AuthProviderUpdated, correlationId,
            AuditChanges.Between(before, after)), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToSummary(provider);
    }

    public async Task<bool> DeleteAsync(UserId actorUserId, AuthProviderId providerId, string? correlationId,
        CancellationToken cancellationToken = default)
    {
        RequireInstanceManager(actorUserId);
        AuthProvider? provider = await dbContext.AuthProviderRepository.GetByIdAsync(providerId, cancellationToken);
        if (provider is null) return false;
        dbContext.AuthProviderRepository.Remove(provider);
        await auditWriter.StageAsync(Audit(provider, actorUserId, AuditActions.AuthProviderDeleted, correlationId,
            null), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ApplyPolicyAsync(AuthProvider provider, AuthProviderInput input, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        TeamId? autoJoin = input.AutoJoinTeamId is { } teamGuid ? new TeamId(teamGuid) : null;
        if (autoJoin is { } teamId && await dbContext.TeamRepository.GetByIdAsync(teamId, cancellationToken) is null)
            throw new InvalidOperationException("The auto-join team does not exist.");
        provider.SetRegistrationPolicy(input.AllowRegistration, autoJoin, input.AllowedDomains,
            input.RequireVerifiedEmail, now);

        var authority = AuthProviderPresets.Authority(provider);
        OutboundTargetCheck check = await urlPolicy.CheckUrlAsync(new Uri(authority), cancellationToken);
        if (!check.Allowed)
            throw new InvalidOperationException($"The provider URL is not allowed: {check.Reason}");

        if (input.Enabled) provider.Enable(now);
        else provider.Disable(now);
    }

    private static AuditEntry Audit(AuthProvider provider, UserId actorUserId, string action, string? correlationId,
        IReadOnlyList<AuditFieldChange>? changes)
    {
        return new AuditEntry(null, actorUserId, action, new AuditTarget("auth_provider", provider.Id.ToString()))
        {
            CorrelationId = correlationId,
            ResourceName = provider.Name,
            Changes = changes,
            Metadata = new Dictionary<string, object?> { ["kind"] = provider.Kind.ToString() }
        };
    }

    private static Dictionary<string, object?> Snapshot(AuthProvider provider)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = provider.Name,
            ["clientId"] = provider.ClientId,
            ["authority"] = provider.Authority,
            ["scopes"] = provider.Scopes,
            ["enabled"] = provider.Enabled,
            ["allowRegistration"] = provider.AllowRegistration,
            ["autoJoinTeamId"] = provider.AutoJoinTeamId?.Value,
            ["allowedDomains"] = provider.AllowedDomains,
            ["requireVerifiedEmail"] = provider.RequireVerifiedEmail
        };
    }

    private AuthProviderSummary ToSummary(AuthProvider provider)
    {
        return new AuthProviderSummary(provider.Id, provider.Kind, provider.Name, provider.ClientId,
            provider.HasClientSecret, provider.Authority, AuthProviderPresets.Scopes(provider), provider.Enabled,
            provider.AllowRegistration, provider.AutoJoinTeamId?.Value, provider.AllowedDomains,
            provider.RequireVerifiedEmail, externalLogin.CallbackUrl(provider.Id)?.AbsoluteUri,
            dbContext.OauthIdentities.Count(identity => identity.ProviderId == provider.Id));
    }

    private void RequireInstanceManager(UserId actorUserId)
    {
        if (!authorization.IsInstanceManager(actorUserId))
            throw new UnauthorizedAccessException("Only instance administrators can manage sign-in providers.");
    }
}
