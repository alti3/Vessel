using System.Net;
using System.Net.Sockets;
using Vessel.Application.OutboundRequests;

namespace Vessel.Infrastructure.OutboundRequests;

/// <summary>
///     Thrown when the outbound request policy refuses a target. The message never contains the URL path or query.
/// </summary>
public sealed class OutboundRequestBlockedException(string reason)
    : HttpRequestException(HttpRequestError.ConnectionError, $"Outbound request blocked: {reason}");

/// <summary>
///     Builds the primary handler for HTTP clients that call user-supplied URLs. DNS resolution happens inside
///     <see cref="SocketsHttpHandler.ConnectCallback" /> and the socket connects only to addresses that pass
///     <see cref="IOutboundUrlPolicy.IsAddressAllowed" />, which pins the checked address and defeats DNS rebinding.
///     Redirects and environment proxies are disabled so neither can route around the check.
/// </summary>
public static class GuardedOutboundHttpHandler
{
    public static SocketsHttpHandler Create(IOutboundUrlPolicy policy, IHostAddressResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(resolver);

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = (context, cancellationToken) =>
                ConnectAsync(policy, resolver, context.DnsEndPoint, cancellationToken)
        };
    }

    internal static async ValueTask<Stream> ConnectAsync(IOutboundUrlPolicy policy, IHostAddressResolver resolver,
        DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        var host = endpoint.Host.Trim('[', ']');
        IPAddress[] candidates = IPAddress.TryParse(host, out IPAddress? literal)
            ? [literal]
            : await resolver.ResolveAsync(host, cancellationToken);

        IPAddress[] allowed = candidates.Where(address => policy.IsAddressAllowed(host, address)).ToArray();
        if (allowed.Length == 0)
            throw new OutboundRequestBlockedException(candidates.Length == 0
                ? "the host could not be resolved."
                : "the host resolves only to addresses that are not allowed.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, endpoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
///     First line of the outbound guard: rejects disallowed schemes, embedded credentials, and malformed hosts before
///     any connection is attempted, and marks the request so telemetry exports only <c>scheme://host[:port]</c>.
/// </summary>
public sealed class OutboundUrlGuardHandler(OutboundUrlPolicy policy) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Options.Set(OutboundRequestRedaction.RedactUrlOption, true);
        OutboundTargetCheck check = policy.CheckUrlShape(request.RequestUri);
        if (!check.Allowed)
            throw new OutboundRequestBlockedException(check.Reason ?? "the URL is not allowed.");

        return base.SendAsync(request, cancellationToken);
    }
}
