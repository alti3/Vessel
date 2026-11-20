using System.Net;
using System.Net.Sockets;
using Vessel.Application.OutboundRequests;

namespace Vessel.Infrastructure.OutboundRequests;

/// <summary>The outbound host did not resolve. This can be temporary, so the caller may retry.</summary>
public sealed class OutboundHostUnresolvedException()
    : IOException("The host could not be resolved.");

/// <summary>
///     Opens TCP connections to user-supplied non-HTTP endpoints (SMTP) under the outbound SSRF policy (SEC-08,
///     upstream <c>SafeSmtpHost::connectionAddress</c>). The host is resolved exactly once; when any answer is a
///     blocked address (loopback, link-local/metadata, reserved, or a private range that is not allow-listed) the
///     connection is refused, otherwise the socket connects only to the checked addresses. Callers then speak the
///     protocol over that socket and verify TLS against the original host name, so a DNS change between validation
///     and connect (rebinding) cannot redirect the connection.
/// </summary>
public sealed class PinnedSocketConnector(IOutboundUrlPolicy policy, IHostAddressResolver resolver)
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(5);

    /// <exception cref="OutboundRequestBlockedException">The host is not allowed.</exception>
    /// <exception cref="OutboundHostUnresolvedException">The host did not resolve.</exception>
    public async Task<Socket> ConnectAsync(string host, int port, TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        var normalized = (host ?? string.Empty).Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0)
            throw new OutboundRequestBlockedException("a host is required.");
        if (port is < 1 or > 65535)
            throw new OutboundRequestBlockedException("the port is not valid.");

        IPAddress[] addresses = await ResolveOnceAsync(normalized, cancellationToken);

        // Reject the whole answer when any address is unsafe: a mixed answer is a classic rebinding setup.
        foreach (IPAddress address in addresses)
            if (!policy.IsAddressAllowed(normalized, address))
                throw new OutboundRequestBlockedException(OutboundUrlPolicy.DenyReason(OutboundUrlPolicy.Classify(address)));

        // Prefer IPv4 like upstream; fall back to the remaining checked addresses.
        IPAddress[] ordered = addresses
            .Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectTimeout > TimeSpan.Zero ? connectTimeout : TimeSpan.FromSeconds(30));

        Exception? lastError = null;
        foreach (IPAddress address in ordered)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
                return socket;
            }
            catch (Exception exception) when (exception is SocketException
                                                  || (exception is OperationCanceledException
                                                      && !cancellationToken.IsCancellationRequested))
            {
                socket.Dispose();
                lastError = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new IOException("Could not connect to the host.", lastError);
    }

    private async Task<IPAddress[]> ResolveOnceAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out IPAddress? literal))
            return [literal];

        IPAddress[] addresses;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResolveTimeout);
            addresses = await resolver.ResolveAsync(host, timeout.Token);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException ||
                                          (exception is OperationCanceledException &&
                                           !cancellationToken.IsCancellationRequested))
        {
            throw new OutboundHostUnresolvedException();
        }

        return addresses.Length == 0 ? throw new OutboundHostUnresolvedException() : addresses;
    }
}
