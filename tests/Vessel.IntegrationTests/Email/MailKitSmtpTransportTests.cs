using System.Net;
using System.Net.Sockets;
using System.Text;
using MailKit.Security;
using Microsoft.Extensions.Options;
using Vessel.Application.Email;
using Vessel.Domain.Notifications;
using Vessel.Domain.Settings;
using Vessel.Infrastructure.Email;
using Vessel.Infrastructure.OutboundRequests;

namespace Vessel.IntegrationTests.Email;

/// <summary>NTF-06 MailKit transport and SEC-08 pinned SMTP connections, against an in-process SMTP fake.</summary>
public sealed class MailKitSmtpTransportTests
{
    private static readonly EmailSender From = new("noreply@example.com", "Vessel");

    [Fact]
    public async Task SendsThroughThePinnedAddressWithTheConfiguredEhloDomain()
    {
        await using var server = FakeSmtpServer.Start();
        var resolver = new CountingResolver("127.0.0.1");
        var transport = CreateTransport(resolver, allowLoopback: true);

        EmailSendResult result = await transport.SendAsync(Settings(server.Port, ehlo: "mx.vessel.example"), From,
            new EmailMessage(["ops@example.com"], "Deployment failed", "Text body", "<p>Html body</p>"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal("mx.vessel.example", server.EhloDomains.Single());
        Assert.Equal("mailer", server.AuthenticatedUsers.Single());
        string data = server.Messages.Single();
        Assert.Contains("Subject: Deployment failed", data, StringComparison.Ordinal);
        Assert.Contains("To: ops@example.com", data, StringComparison.Ordinal);
        Assert.Contains("From: Vessel <noreply@example.com>", data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoopbackTargetsAreRefusedWithoutConnecting()
    {
        await using var server = FakeSmtpServer.Start();
        var transport = CreateTransport(new CountingResolver("127.0.0.1"), allowLoopback: false);

        EmailSendResult result = await transport.SendAsync(Settings(server.Port), From,
            new EmailMessage(["ops@example.com"], "S", "B"), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(NotificationFailureKind.Configuration, result.FailureKind);
        Assert.Contains("loopback", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, server.Connections);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("169.254.169.254")]
    [InlineData("93.184.216.34,127.0.0.1")]
    public async Task PrivateMetadataAndMixedAnswersAreRefused(string answers)
    {
        var transport = CreateTransport(new CountingResolver(answers.Split(',')), allowLoopback: false);

        EmailSendResult result = await transport.SendAsync(Settings(2525), From,
            new EmailMessage(["ops@example.com"], "S", "B"), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(NotificationFailureKind.Configuration, result.FailureKind);
        Assert.StartsWith("The SMTP host is not allowed", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnresolvableHostIsTransient()
    {
        var transport = CreateTransport(new CountingResolver(), allowLoopback: true);

        EmailSendResult result = await transport.SendAsync(Settings(2525), From,
            new EmailMessage(["ops@example.com"], "S", "B"), TestContext.Current.CancellationToken);

        Assert.Equal(NotificationFailureKind.Transient, result.FailureKind);
    }

    [Fact]
    public async Task RefusedLoginIsAConfigurationErrorWithoutTheServerReply()
    {
        await using var server = FakeSmtpServer.Start(rejectAuth: true);
        var transport = CreateTransport(new CountingResolver("127.0.0.1"), allowLoopback: true);

        EmailSendResult result = await transport.SendAsync(Settings(server.Port), From,
            new EmailMessage(["ops@example.com"], "S", "B"), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(NotificationFailureKind.Configuration, result.FailureKind);
        Assert.DoesNotContain("top-secret", result.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSmtpServer.AuthRejectText, result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TemporaryRecipientRejectionIsTransient()
    {
        await using var server = FakeSmtpServer.Start(tempFailRecipients: true);
        var transport = CreateTransport(new CountingResolver("127.0.0.1"), allowLoopback: true);

        EmailSendResult result = await transport.SendAsync(Settings(server.Port), From,
            new EmailMessage(["ops@example.com"], "S", "B"), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(NotificationFailureKind.Transient, result.FailureKind);
    }

    [Fact]
    public async Task RequiredStartTlsWithoutServerSupportIsAConfigurationError()
    {
        await using var server = FakeSmtpServer.Start();
        var transport = CreateTransport(new CountingResolver("127.0.0.1"), allowLoopback: true);

        EmailSendResult result = await transport.SendAsync(Settings(server.Port) with
        {
            Encryption = SmtpEncryption.StartTls
        }, From, new EmailMessage(["ops@example.com"], "S", "B"), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(NotificationFailureKind.Configuration, result.FailureKind);
        Assert.Empty(server.Messages);
    }

    [Fact]
    public void EncryptionModesMapToMailKitOptions()
    {
        Assert.Equal(SecureSocketOptions.None, MailKitSmtpTransport.ToSecureSocketOptions(SmtpEncryption.None));
        Assert.Equal(SecureSocketOptions.StartTls, MailKitSmtpTransport.ToSecureSocketOptions(SmtpEncryption.StartTls));
        Assert.Equal(SecureSocketOptions.SslOnConnect, MailKitSmtpTransport.ToSecureSocketOptions(SmtpEncryption.Tls));
    }

    [Fact]
    public void SubjectLineBreaksCannotInjectHeaders()
    {
        var mime = MailKitSmtpTransport.BuildMessage(From,
            new EmailMessage(["ops@example.com"], "Hello\r\nBcc: attacker@example.com", "B"));

        Assert.Empty(mime.Bcc);
        Assert.DoesNotContain('\n', mime.Subject ?? string.Empty);
    }

    private static MailKitSmtpTransport CreateTransport(CountingResolver resolver, bool allowLoopback)
    {
        var policy = new OutboundUrlPolicy(Options.Create(new OutboundRequestOptions { AllowLoopback = allowLoopback }),
            resolver);
        return new MailKitSmtpTransport(new PinnedSocketConnector(policy, resolver));
    }

    private static SmtpTransportSettings Settings(int port, string? ehlo = null)
    {
        return new SmtpTransportSettings("smtp.vessel.test", port, SmtpEncryption.None, "mailer", "top-secret", ehlo,
            TimeSpan.FromSeconds(10));
    }

    private sealed class CountingResolver(params string[] addresses) : IHostAddressResolver
    {
        public int Calls { get; private set; }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());
        }
    }
}

/// <summary>Minimal SMTP server (no TLS) for transport tests. Records EHLO domains, logins, and message data.</summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    public const string AuthRejectText = "5.7.8 credentials top-secret rejected";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly bool _rejectAuth;
    private readonly bool _tempFailRecipients;
    private int _connections;

    private FakeSmtpServer(bool rejectAuth, bool tempFailRecipients)
    {
        _rejectAuth = rejectAuth;
        _tempFailRecipients = tempFailRecipients;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _loop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int Connections => Volatile.Read(ref _connections);
    public List<string> EhloDomains { get; } = [];
    public List<string> AuthenticatedUsers { get; } = [];
    public List<string> Messages { get; } = [];

    public static FakeSmtpServer Start(bool rejectAuth = false, bool tempFailRecipients = false)
    {
        return new FakeSmtpServer(rejectAuth, tempFailRecipients);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException
                                              or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
            Interlocked.Increment(ref _connections);
            using (client)
            {
                try
                {
                    await ServeAsync(client.GetStream());
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private async Task ServeAsync(NetworkStream stream)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };
        await writer.WriteLineAsync("220 fake.smtp ESMTP");
        while (await reader.ReadLineAsync(_stop.Token) is { } line)
        {
            var verb = line.Split(' ', 2)[0].ToUpperInvariant();
            switch (verb)
            {
                case "EHLO":
                    EhloDomains.Add(line.Split(' ', 2)[1]);
                    await writer.WriteLineAsync("250-fake.smtp");
                    await writer.WriteLineAsync("250-AUTH PLAIN");
                    await writer.WriteLineAsync("250 8BITMIME");
                    break;
                case "AUTH":
                    var parts = line.Split(' ');
                    var initial = parts.Length > 2 ? parts[2] : null;
                    if (initial is null)
                    {
                        await writer.WriteLineAsync("334 ");
                        initial = await reader.ReadLineAsync(_stop.Token);
                    }

                    if (_rejectAuth)
                    {
                        await writer.WriteLineAsync($"535 {AuthRejectText}");
                        break;
                    }

                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(initial ?? string.Empty))
                        .Split('\0');
                    AuthenticatedUsers.Add(decoded.Length > 1 ? decoded[1] : string.Empty);
                    await writer.WriteLineAsync("235 2.7.0 ok");
                    break;
                case "MAIL":
                    await writer.WriteLineAsync("250 2.1.0 ok");
                    break;
                case "RCPT":
                    await writer.WriteLineAsync(_tempFailRecipients ? "451 4.3.0 try later" : "250 2.1.5 ok");
                    break;
                case "DATA":
                    await writer.WriteLineAsync("354 go ahead");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync(_stop.Token) is { } dataLine && dataLine != ".")
                        data.Append(dataLine).Append('\n');
                    Messages.Add(data.ToString());
                    await writer.WriteLineAsync("250 2.0.0 queued");
                    break;
                case "RSET":
                case "NOOP":
                    await writer.WriteLineAsync("250 ok");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 bye");
                    return;
                default:
                    await writer.WriteLineAsync("502 not implemented");
                    break;
            }
        }
    }
}
