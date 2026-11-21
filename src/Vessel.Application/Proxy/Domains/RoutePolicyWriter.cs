using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Proxy;
using Vessel.Domain.Secrets;

namespace Vessel.Application.Proxy.Domains;

/// <summary>
///     Merges a <see cref="RoutePolicyRequest" /> into the current <see cref="RoutePolicy" />. The basic-auth password is
///     write-only: it is stored in (or replaced inside) the secret vault and only its reference is kept on the route.
/// </summary>
public sealed class RoutePolicyWriter(ISecretVault secretVault)
{
    public const int MinBasicAuthPasswordLength = 8;
    public const int MaxBasicAuthPasswordLength = 256;

    public async Task<RoutePolicy> MergeAsync(
        RoutePolicy current,
        RoutePolicyRequest? request,
        TeamId teamId,
        SecretScope scope,
        string secretKey,
        SecretTarget secretTarget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (request is null) return current;
        if (request.RedirectMode is { } mode && !Enum.IsDefined(mode))
            throw new DomainException("Redirect mode must be Both, Www or NonWww.");

        RouteBasicAuth? basicAuth = current.BasicAuth;
        var enable = request.BasicAuthEnabled ?? (current.BasicAuth is not null ||
                                                  request.BasicAuthUsername is not null ||
                                                  request.BasicAuthPassword is not null);
        if (!enable)
        {
            basicAuth = null;
        }
        else
        {
            var username = request.BasicAuthUsername ?? current.BasicAuth?.Username
                ?? throw new DomainException("Basic auth requires a username.");
            SecretReferenceId reference;
            if (request.BasicAuthPassword is not null)
            {
                ValidatePassword(request.BasicAuthPassword);
                if (current.BasicAuth is not null)
                {
                    await secretVault.ReplaceAsync(current.BasicAuth.PasswordSecretReferenceId,
                        request.BasicAuthPassword, cancellationToken);
                    reference = current.BasicAuth.PasswordSecretReferenceId;
                }
                else
                {
                    SecretReference stored = await secretVault.StoreAsync(teamId, scope, secretKey,
                        request.BasicAuthPassword, new SecretPolicy(false, false, false), secretTarget,
                        cancellationToken);
                    reference = stored.Id;
                }
            }
            else
            {
                reference = current.BasicAuth?.PasswordSecretReferenceId
                            ?? throw new DomainException("Basic auth requires a password.");
            }

            basicAuth = new RouteBasicAuth(username, reference);
        }

        return new RoutePolicy(
            request.ForceHttps ?? current.ForceHttps,
            request.RedirectMode ?? current.RedirectMode,
            request.PermanentRedirect ?? current.PermanentRedirect,
            request.Compression ?? current.Compression,
            request.NoIndex ?? current.NoIndex,
            request.StripPathPrefix ?? current.StripPathPrefix,
            basicAuth);
    }

    private static void ValidatePassword(string password)
    {
        if (password.Length is < MinBasicAuthPasswordLength or > MaxBasicAuthPasswordLength)
            throw new DomainException(
                $"Basic auth password must be between {MinBasicAuthPasswordLength} and {MaxBasicAuthPasswordLength} characters.");
        if (password.Any(char.IsControl))
            throw new DomainException("Basic auth password must not contain control characters.");
    }
}
