using System.Security.Cryptography;
using System.Text;
using Vessel.Application.Auditing;
using Vessel.Application.Common;
using Vessel.Application.Instance;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Auth;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Auth.External;

/// <summary>
///     External sign-in (IAM-07, Coolify <c>OauthLoginService</c> parity) with OAuth 2.0 authorization code + PKCE.
///     <list type="bullet">
///         <item>The callback URL is built from <c>Vessel:Host:PublicUrl</c>, never from the request Host header.</item>
///         <item>
///             Identities are looked up by provider instance + subject. An identity linked under another issuer is
///             refused instead of silently re-linked.
///         </item>
///         <item>
///             An existing account is linked only when the provider verified the email, and only if the account
///             has no linked identity yet (one identity per account).
///         </item>
///         <item>
///             New accounts follow the registration policy: instance registration open, a pending invitation for the
///             address, or the provider's own "allow registration" switch. Otherwise they are refused.
///         </item>
///         <item>Accounts with two-factor authentication must pass the second factor before a session is issued.</item>
///         <item>Google Workspace (<c>hd</c>) and email-domain restrictions are enforced per provider.</item>
///     </list>
/// </summary>
public sealed class ExternalLoginService(
    IVesselDbContext dbContext,
    IOAuthProviderClient client,
    ISecretCipher cipher,
    InstanceLinkBuilder links,
    InstanceSettingsService instanceSettings,
    VesselAuthenticationService authentication,
    IAuditWriter auditWriter,
    ITokenGenerator tokenGenerator,
    TimeProvider timeProvider)
{
    public const string ClientSecretPurpose = "auth-provider.client-secret";

    public IReadOnlyList<AuthProviderButton> ListSignInProviders()
    {
        return dbContext.AuthProviders
            .Where(provider => provider.Enabled)
            .OrderBy(provider => provider.Name)
            .AsEnumerable()
            .Where(provider => provider.CanSignIn)
            .Select(provider => new AuthProviderButton(provider.Id, provider.Name, provider.Kind))
            .ToArray();
    }

    /// <summary>The redirect URI registered at the provider: <c>{PublicUrl}/auth/oauth/{id}/callback</c>.</summary>
    public Uri? CallbackUrl(AuthProviderId providerId)
    {
        return links.Build($"auth/oauth/{providerId.Value:D}/callback");
    }

    public async Task<ExternalLoginChallenge> BeginAsync(
        AuthProviderId providerId,
        string returnUrl,
        CancellationToken cancellationToken = default)
    {
        AuthProvider provider = await GetUsableProviderAsync(providerId, cancellationToken);
        Uri redirectUri = CallbackUrl(providerId)
                          ?? throw new ExternalLoginException(ExternalLoginDenials.InstanceUrlMissing,
                              "Set Vessel:Host:PublicUrl before using external sign-in.");
        OAuthEndpoints endpoints = await ResolveAsync(provider, cancellationToken);

        var state = tokenGenerator.GenerateUrlSafeToken();
        var nonce = tokenGenerator.GenerateUrlSafeToken();
        var verifier = tokenGenerator.GenerateUrlSafeToken(48);
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", provider.ClientId),
            new("redirect_uri", redirectUri.AbsoluteUri),
            new("scope", AuthProviderPresets.Scopes(provider)),
            new("state", state),
            new("code_challenge", CodeChallenge(verifier)),
            new("code_challenge_method", "S256")
        };
        if (endpoints.IsOpenIdConnect) query.Add(new("nonce", nonce));
        if (provider.Kind == AuthProviderKind.Google && provider.GetAllowedDomains() is [var domain] && domain != "*")
            query.Add(new("hd", domain));

        var separator = endpoints.AuthorizationEndpoint.Query.Length > 0 ? "&" : "?";
        var url = new Uri(endpoints.AuthorizationEndpoint.AbsoluteUri + separator + string.Join('&',
            query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
        return new ExternalLoginChallenge(providerId, url, state, nonce, verifier, returnUrl);
    }

    /// <summary>
    ///     Completes the callback. The Web layer has already matched <c>state</c> against the stored challenge.
    /// </summary>
    public async Task<ExternalLoginResult> CompleteAsync(
        ExternalLoginChallenge challenge,
        string code,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var method = "oauth:" + challenge.ProviderId.Value.ToString("N")[..8];
        AuthProvider provider;
        ExternalUserInfo info;
        try
        {
            provider = await GetUsableProviderAsync(challenge.ProviderId, cancellationToken);
            method = "oauth:" + provider.Kind.ToString().ToLowerInvariant();
            Uri redirectUri = CallbackUrl(provider.Id)
                              ?? throw new ExternalLoginException(ExternalLoginDenials.InstanceUrlMissing,
                                  "Instance URL is not configured.");
            OAuthEndpoints endpoints = await ResolveAsync(provider, cancellationToken);
            var secret = cipher.Unprotect(provider.ProtectedClientSecret!, ClientSecretPurpose);
            info = await client.ExchangeCodeAsync(new OAuthExchangeRequest(provider.Kind, provider.ClientId, secret,
                endpoints, code, redirectUri, challenge.CodeVerifier, challenge.Nonce,
                provider.Kind == AuthProviderKind.GitHub ? AuthProviderPresets.Authority(provider) : null),
                cancellationToken);
        }
        catch (ExternalLoginException exception)
        {
            return await DenyAsync(challenge.ProviderId, null, exception.Reason, method, correlationId,
                cancellationToken);
        }

        return await SignInAsync(provider, info, method, correlationId, cancellationToken);
    }

    /// <summary>Applies the link and registration rules to a provider-attested identity.</summary>
    internal async Task<ExternalLoginResult> SignInAsync(
        AuthProvider provider,
        ExternalUserInfo info,
        string method,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var email = string.IsNullOrWhiteSpace(info.Email) ? null : info.Email.Trim().ToLowerInvariant();

        if (!DomainAllowed(provider, info, email))
            return await DenyAsync(provider.Id, null, ExternalLoginDenials.DomainNotAllowed, method, correlationId,
                cancellationToken);
        if (provider.RequireVerifiedEmail && !info.EmailVerified)
            return await DenyAsync(provider.Id, null, ExternalLoginDenials.EmailUnverified, method, correlationId,
                cancellationToken);

        OauthIdentity? identity = dbContext.OauthIdentities
            .SingleOrDefault(candidate => candidate.ProviderId == provider.Id && candidate.Subject == info.Subject);
        User? user;
        var linked = false;
        var created = false;
        if (identity is not null)
        {
            if (!string.Equals(identity.Issuer, info.Issuer, StringComparison.Ordinal))
                return await DenyAsync(provider.Id, identity.UserId, ExternalLoginDenials.IssuerMismatch, method,
                    correlationId, cancellationToken);
            user = await dbContext.UserRepository.GetByIdAsync(identity.UserId, cancellationToken);
            if (user is null)
                return await DenyAsync(provider.Id, null, ExternalLoginDenials.ProviderUnavailable, method,
                    correlationId, cancellationToken);
            identity.RecordLogin(email, now);
        }
        else
        {
            if (email is null || TryParseEmail(email) is not { } address)
                return await DenyAsync(provider.Id, null, ExternalLoginDenials.EmailMissing, method, correlationId,
                    cancellationToken);

            user = dbContext.Users.SingleOrDefault(candidate => candidate.Email == address);
            if (user is not null)
            {
                // Linking by email is account takeover unless the provider attests the address; this is checked
                // before the already-linked check so an unverified email cannot probe for linked accounts.
                if (!info.EmailVerified)
                    return await DenyAsync(provider.Id, user.Id, ExternalLoginDenials.EmailUnverified, method,
                        correlationId, cancellationToken);
                if (dbContext.OauthIdentities.Any(candidate => candidate.UserId == user.Id))
                    return await DenyAsync(provider.Id, user.Id, ExternalLoginDenials.AlreadyLinked, method,
                        correlationId, cancellationToken);
                linked = true;
            }
            else
            {
                if (!provider.AllowRegistration && !instanceSettings.CanRegister(address.Value))
                    return await DenyAsync(provider.Id, null, ExternalLoginDenials.RegistrationDisabled, method,
                        correlationId, cancellationToken);
                user = await CreateUserAsync(provider, info, address, now, cancellationToken);
                created = true;
            }

            await dbContext.OauthIdentityRepository.AddAsync(
                OauthIdentity.Link(user.Id, provider.Id, info.Issuer, info.Subject, email, now), cancellationToken);
            if (linked)
                await auditWriter.StageAsync(new AuditEntry(null, user.Id, AuditActions.OAuthIdentityLinked,
                    new AuditTarget("user", user.Id.ToString()))
                {
                    CorrelationId = correlationId,
                    ResourceName = user.Name.Value,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["providerId"] = provider.Id.Value,
                        ["providerKind"] = provider.Kind.ToString()
                    }
                }, cancellationToken);
        }

        if (user.IsLockedOut(now))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return await DenyAsync(provider.Id, user.Id, ExternalLoginDenials.LockedOut, method, correlationId,
                cancellationToken);
        }

        if (user.TwoFactorConfirmedAt.HasValue)
        {
            await auditWriter.StageAsync(new AuditEntry(null, user.Id, AuditActions.OAuthTwoFactorChallenged,
                new AuditTarget("user", user.Id.ToString()))
            {
                CorrelationId = correlationId,
                Metadata = new Dictionary<string, object?> { ["method"] = method, ["newAccount"] = created }
            }, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new ExternalLoginResult(ExternalLoginStatus.TwoFactorRequired, null, user.Id, null, method);
        }

        AuthenticatedUser signedIn = await authentication.CompleteLoginAsync(user, method, correlationId, now,
            cancellationToken);
        return new ExternalLoginResult(ExternalLoginStatus.SignedIn, signedIn, null, null, method);
    }

    private async Task<User> CreateUserAsync(AuthProvider provider, ExternalUserInfo info, EmailAddress address,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var isFirstUser = !dbContext.Users.Any();
        var name = string.IsNullOrWhiteSpace(info.Name) ? address.Value.Split('@')[0] : info.Name.Trim();
        if (name.Length > 120) name = name[..120];
        var user = User.Create(new DisplayName(name), address, now);
        if (info.EmailVerified) user.VerifyEmail(now);
        if (isFirstUser) user.GrantInstanceAdmin(now);
        var team = Team.Create(new DisplayName($"{user.Name.Value}'s Team"), user.Id, true, now);
        user.SetCurrentTeam(team.Id);
        await dbContext.UserRepository.AddAsync(user, cancellationToken);
        await dbContext.TeamRepository.AddAsync(team, cancellationToken);

        TeamId? joined = null;
        if (provider.AutoJoinTeamId is { } autoJoinTeamId
            && await dbContext.TeamRepository.GetByIdAsync(autoJoinTeamId, cancellationToken) is { } autoJoin)
        {
            // New accounts join as members only; elevated roles are always granted by a team manager.
            autoJoin.AddMember(user.Id, TeamRole.Member, now);
            joined = autoJoin.Id;
        }

        await auditWriter.StageAsync(new AuditEntry(team.Id, user.Id, AuditActions.UserRegistered,
            new AuditTarget("user", user.Id.ToString()))
        {
            ResourceName = user.Name.Value,
            Metadata = new Dictionary<string, object?>
            {
                ["method"] = "oauth:" + provider.Kind.ToString().ToLowerInvariant(),
                ["providerId"] = provider.Id.Value,
                ["autoJoinedTeamId"] = joined?.Value,
                ["instanceAdmin"] = isFirstUser
            }
        }, cancellationToken);
        return user;
    }

    private static bool DomainAllowed(AuthProvider provider, ExternalUserInfo info, string? email)
    {
        IReadOnlyList<string> allowed = provider.GetAllowedDomains();
        if (allowed.Count == 0) return true;
        if (provider.Kind == AuthProviderKind.Google)
        {
            var hostedDomain = info.HostedDomain?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(hostedDomain)) return false;
            return allowed.Contains("*") || allowed.Contains(hostedDomain);
        }

        if (email is null || !info.EmailVerified) return false;
        var emailDomain = email[(email.LastIndexOf('@') + 1)..];
        return allowed.Contains(emailDomain);
    }

    private async Task<ExternalLoginResult> DenyAsync(AuthProviderId providerId, UserId? userId, string reason,
        string method, string? correlationId, CancellationToken cancellationToken)
    {
        await auditWriter.RecordAsync(new AuditEntry(null, null, AuditActions.OAuthLoginDenied,
            userId is { } id ? new AuditTarget("user", id.ToString()) : new AuditTarget("auth_provider", providerId.ToString()))
        {
            CorrelationId = correlationId,
            Level = AuditLevel.Warning,
            Metadata = new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["providerId"] = providerId.Value,
                ["method"] = method
            }
        }, cancellationToken);
        return ExternalLoginResult.Denied(reason, method);
    }

    private async Task<AuthProvider> GetUsableProviderAsync(AuthProviderId providerId,
        CancellationToken cancellationToken)
    {
        AuthProvider? provider = await dbContext.AuthProviderRepository.GetByIdAsync(providerId, cancellationToken);
        if (provider is null || !provider.CanSignIn)
            throw new ExternalLoginException(ExternalLoginDenials.ProviderUnavailable,
                "The sign-in provider is not available.");
        return provider;
    }

    private async Task<OAuthEndpoints> ResolveAsync(AuthProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            return await client.ResolveEndpointsAsync(provider.Kind, AuthProviderPresets.Authority(provider),
                cancellationToken);
        }
        catch (ExternalLoginException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException
                                              or TaskCanceledException)
        {
            throw new ExternalLoginException(ExternalLoginDenials.ProviderError,
                "The sign-in provider could not be reached.");
        }
    }

    private static EmailAddress? TryParseEmail(string email)
    {
        try
        {
            return new EmailAddress(email);
        }
        catch (Vessel.Domain.Common.DomainException)
        {
            return null;
        }
    }

    internal static string CodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
