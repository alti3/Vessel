using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.Proxy;

/// <summary>
///     www handling for a route (upstream <c>redirect</c>): <see cref="Both" /> serves www and non-www as configured,
///     <see cref="Www" /> redirects non-www hosts to <c>www.</c>, <see cref="NonWww" /> redirects <c>www.</c> hosts to the
///     bare host.
/// </summary>
public enum RouteRedirectMode
{
    Both = 0,
    Www = 1,
    NonWww = 2
}

/// <summary>
///     HTTP basic authentication for a route. Only the username and a reference to the password secret are stored; the
///     password is hashed (bcrypt) at render time and never persisted or returned in clear.
/// </summary>
public sealed partial record RouteBasicAuth
{
    public const int MaxUsernameLength = 64;

    public RouteBasicAuth(string username, SecretReferenceId passwordSecretReferenceId)
    {
        var normalized = DomainValidation.Required(username, nameof(username), MaxUsernameLength);
        // htpasswd lines are `user:hash`; a colon (or anything outside this set) would corrupt the line.
        if (!UsernamePattern().IsMatch(normalized))
            throw new DomainException("Basic auth username may only contain letters, digits and . _ @ -.");
        Username = normalized;
        PasswordSecretReferenceId = passwordSecretReferenceId;
    }

    public string Username { get; }

    public SecretReferenceId PasswordSecretReferenceId { get; }

    [GeneratedRegex("^[A-Za-z0-9._@-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}

/// <summary>
///     Per-route HTTP behavior (upstream: force HTTPS, redirect direction, permanent redirects, gzip, noindex, basic
///     auth, strip prefix). Redirects are temporary unless <see cref="PermanentRedirect" /> is set, because browsers cache
///     301 responses indefinitely.
/// </summary>
public sealed record RoutePolicy(
    bool ForceHttps,
    RouteRedirectMode RedirectMode,
    bool PermanentRedirect,
    bool Compression,
    bool NoIndex,
    bool StripPathPrefix,
    RouteBasicAuth? BasicAuth)
{
    /// <summary>Upstream defaults: force HTTPS on, serve both www forms, temporary redirects, gzip on, strip prefix on.</summary>
    public static RoutePolicy Default { get; } = new(true, RouteRedirectMode.Both, false, true, false, true, null);
}
