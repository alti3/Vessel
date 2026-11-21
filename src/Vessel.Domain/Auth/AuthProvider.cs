using Vessel.Domain.Common;

namespace Vessel.Domain.Auth;

/// <summary>Kinds of external sign-in providers (IAM-07).</summary>
public enum AuthProviderKind
{
    /// <summary>Any OpenID Connect provider found through <c>{authority}/.well-known/openid-configuration</c>.</summary>
    Oidc = 0,

    /// <summary>GitHub (or GitHub Enterprise Server) OAuth app; not OIDC.</summary>
    GitHub = 1,

    /// <summary>GitLab (gitlab.com or self-managed) through its OpenID Connect support.</summary>
    GitLab = 2,

    /// <summary>Google accounts through OpenID Connect, with an optional Workspace domain restriction.</summary>
    Google = 3
}

/// <summary>
///     An instance-level external sign-in provider (IAM-07), configured by instance administrators. The client secret
///     is stored only as a key-ring protected envelope (never in plaintext, never returned). Each row is its own
///     provider instance: identities are keyed by this id and the provider subject, so two GitLab instances or two OIDC
///     issuers never collide.
/// </summary>
public sealed class AuthProvider : Entity<AuthProviderId>
{
    public const int NameMaxLength = 120;
    public const int ClientIdMaxLength = 512;
    public const int ProtectedSecretMaxLength = 4096;
    public const int AuthorityMaxLength = 512;
    public const int ScopesMaxLength = 512;
    public const int AllowedDomainsMaxLength = 1000;

    private AuthProvider()
    {
    }

    private AuthProvider(AuthProviderId id, AuthProviderKind kind, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        Kind = kind;
    }

    public AuthProviderKind Kind { get; private set; }

    /// <summary>Label on the sign-in button, for example "Company SSO".</summary>
    public string Name { get; private set; } = string.Empty;

    public string ClientId { get; private set; } = string.Empty;

    /// <summary>Key-ring protected client secret (<c>ISecretCipher.Protect</c>), or <c>null</c> when not set.</summary>
    public string? ProtectedClientSecret { get; private set; }

    /// <summary>OIDC issuer, GitLab base URL or GitHub Enterprise base URL; <c>null</c> uses the preset default.</summary>
    public string? Authority { get; private set; }

    /// <summary>Space-separated scopes; <c>null</c> uses the preset default.</summary>
    public string? Scopes { get; private set; }

    public bool Enabled { get; private set; }

    /// <summary>
    ///     Lets this provider create accounts even when instance registration is closed (Coolify OIDC
    ///     <c>allow_registration</c>). When off, new accounts follow the instance registration policy.
    /// </summary>
    public bool AllowRegistration { get; private set; }

    /// <summary>Accounts created through this provider join this team as members (Coolify auto-join root team).</summary>
    public TeamId? AutoJoinTeamId { get; private set; }

    /// <summary>
    ///     Comma-separated domains. Google: the Workspace (<c>hd</c> claim) must be one of them (<c>*</c> accepts any
    ///     Workspace but no consumer accounts). Other providers: the verified email domain must be one of them.
    /// </summary>
    public string? AllowedDomains { get; private set; }

    /// <summary>
    ///     Requires a provider-verified email for every sign-in (default on). Linking an existing account always
    ///     requires a verified email, whatever this setting says.
    /// </summary>
    public bool RequireVerifiedEmail { get; private set; } = true;

    public bool HasClientSecret => !string.IsNullOrWhiteSpace(ProtectedClientSecret);

    public bool CanSignIn => Enabled && HasClientSecret && !string.IsNullOrWhiteSpace(ClientId);

    public static AuthProvider Create(
        AuthProviderKind kind,
        string name,
        string clientId,
        string? protectedClientSecret,
        string? authority,
        string? scopes,
        DateTimeOffset now)
    {
        if (!Enum.IsDefined(kind)) throw new DomainException("Unknown authentication provider kind.");
        var provider = new AuthProvider(AuthProviderId.New(), kind, now);
        provider.Configure(name, clientId, authority, scopes, now);
        if (protectedClientSecret is not null) provider.ReplaceClientSecret(protectedClientSecret, now);
        return provider;
    }

    public void Configure(string name, string clientId, string? authority, string? scopes, DateTimeOffset now)
    {
        Name = DomainValidation.Required(name, nameof(Name), NameMaxLength);
        ClientId = DomainValidation.Required(clientId, nameof(ClientId), ClientIdMaxLength);
        Authority = NormalizeAuthority(authority);
        if (Kind == AuthProviderKind.Oidc && Authority is null)
            throw new DomainException("An OpenID Connect provider needs its issuer URL.");
        Scopes = NormalizeScopes(scopes);
        Touch(now);
    }

    public void ReplaceClientSecret(string protectedClientSecret, DateTimeOffset now)
    {
        ProtectedClientSecret = DomainValidation.Required(protectedClientSecret, nameof(ProtectedClientSecret),
            ProtectedSecretMaxLength);
        Touch(now);
    }

    public void SetRegistrationPolicy(
        bool allowRegistration,
        TeamId? autoJoinTeamId,
        string? allowedDomains,
        bool requireVerifiedEmail,
        DateTimeOffset now)
    {
        AllowRegistration = allowRegistration;
        AutoJoinTeamId = autoJoinTeamId;
        AllowedDomains = NormalizeDomains(allowedDomains);
        RequireVerifiedEmail = requireVerifiedEmail;
        Touch(now);
    }

    public void Enable(DateTimeOffset now)
    {
        if (!HasClientSecret) throw new DomainException("Set the client secret before enabling the provider.");
        Enabled = true;
        Touch(now);
    }

    public void Disable(DateTimeOffset now)
    {
        Enabled = false;
        Touch(now);
    }

    /// <summary>The configured domain list (lower case), or an empty list when unrestricted.</summary>
    public IReadOnlyList<string> GetAllowedDomains()
    {
        return string.IsNullOrWhiteSpace(AllowedDomains)
            ? []
            : AllowedDomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? NormalizeAuthority(string? authority)
    {
        if (string.IsNullOrWhiteSpace(authority)) return null;
        var value = authority.Trim().TrimEnd('/');
        if (value.Length > AuthorityMaxLength) throw new DomainException("The provider URL is too long.");
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new DomainException("The provider URL must be an absolute http(s) URL without credentials or query.");
        return value;
    }

    private static string? NormalizeScopes(string? scopes)
    {
        if (string.IsNullOrWhiteSpace(scopes)) return null;
        var normalized = string.Join(' ', scopes
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal));
        if (normalized.Length > ScopesMaxLength) throw new DomainException("Too many scopes.");
        if (normalized.Any(character => char.IsControl(character) || character is '"' or '\\'))
            throw new DomainException("Scopes contain invalid characters.");
        return normalized;
    }

    private static string? NormalizeDomains(string? domains)
    {
        if (string.IsNullOrWhiteSpace(domains)) return null;
        var items = domains.Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(domain => domain.TrimStart('@').ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (items.Any(domain => domain != "*" && (domain.Length > 253 || !domain.Contains('.')
                                                  || domain.Any(character => !(char.IsAsciiLetterOrDigit(character)
                                                      || character is '.' or '-')))))
            throw new DomainException("Allowed domains must be domain names such as example.com.");
        var normalized = string.Join(',', items);
        if (normalized.Length > AllowedDomainsMaxLength) throw new DomainException("Too many allowed domains.");
        return normalized;
    }
}

/// <summary>
///     An external identity linked to a Vessel account (IAM-07, Coolify <c>oauth_identities</c>). It is keyed by the
///     provider instance and the provider's stable subject (never by email), and stores the issuer it was linked
///     under so an issuer change is detected instead of silently re-linking. An account links at most one identity.
/// </summary>
public sealed class OauthIdentity : Entity<OauthIdentityId>
{
    public const int IssuerMaxLength = 512;
    public const int SubjectMaxLength = 255;
    public const int EmailMaxLength = 320;

    private OauthIdentity()
    {
    }

    private OauthIdentity(
        OauthIdentityId id,
        UserId userId,
        AuthProviderId providerId,
        string issuer,
        string subject,
        string? email,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        UserId = userId;
        ProviderId = providerId;
        Issuer = issuer;
        Subject = subject;
        Email = email;
        LastLoginAt = createdAt;
    }

    public UserId UserId { get; private set; }

    public AuthProviderId ProviderId { get; private set; }

    public string Issuer { get; private set; } = string.Empty;

    public string Subject { get; private set; } = string.Empty;

    /// <summary>Email the provider reported at the last sign-in (informational; never used for lookup).</summary>
    public string? Email { get; private set; }

    public DateTimeOffset LastLoginAt { get; private set; }

    public static OauthIdentity Link(
        UserId userId,
        AuthProviderId providerId,
        string issuer,
        string subject,
        string? email,
        DateTimeOffset now)
    {
        return new OauthIdentity(
            OauthIdentityId.New(),
            userId,
            providerId,
            DomainValidation.Required(issuer, nameof(Issuer), IssuerMaxLength),
            DomainValidation.Required(subject, nameof(Subject), SubjectMaxLength),
            DomainValidation.Optional(email, nameof(Email), EmailMaxLength),
            now);
    }

    public void RecordLogin(string? email, DateTimeOffset now)
    {
        Email = DomainValidation.Optional(email, nameof(Email), EmailMaxLength);
        LastLoginAt = now;
        Touch(now);
    }
}
