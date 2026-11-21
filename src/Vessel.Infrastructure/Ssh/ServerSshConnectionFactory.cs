using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vessel.Application.Servers;
using Vessel.Application.Ssh;
using Vessel.Domain;
using Vessel.Domain.Servers;

namespace Vessel.Infrastructure.Ssh;

/// <summary>
///     Singleton SSH connection factory. Connection data (host key pin, key reference) is resolved in a fresh DI scope
///     by <see cref="ServerSshConnectionService" />; the private key is written to an owner-only file under
///     <see cref="SshRuntimeFiles" /> that lives only while leases use it (plus a short idle window, so a burst of
///     commands does not decrypt the key for every command) and is removed afterwards and on shutdown.
/// </summary>
public sealed class ServerSshConnectionFactory : IServerSshConnectionFactory, IDisposable
{
    private static readonly TimeSpan IdleKeyLifetime = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, CachedKeyFile> _keys = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly ILogger<ServerSshConnectionFactory> _logger;
    private readonly IOptions<SshOptions> _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly SshRuntimeFiles _files;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _sweeper;

    public ServerSshConnectionFactory(
        IServiceScopeFactory scopes,
        SshRuntimeFiles files,
        IOptions<SshOptions> options,
        TimeProvider timeProvider,
        ILogger<ServerSshConnectionFactory> logger)
    {
        _scopes = scopes;
        _files = files;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        try
        {
            _files.PurgeKeys();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not purge stale SSH key files.");
        }

        _sweeper = timeProvider.CreateTimer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task<SshConnectionLease> AcquireAsync(
        ServerExecutionTarget target,
        SshKeyId? keyOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.ConnectionType != ServerConnectionType.Ssh)
            throw new InvalidOperationException("Only SSH servers have SSH connections.");

        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ServerSshConnectionService>();
        PreparedSshConnection prepared = await service.PrepareAsync(target.ServerId, keyOverride, cancellationToken);

        string? keyPath = null;
        string? cacheKey = null;
        if (prepared.KeyId.HasValue)
        {
            cacheKey = $"{prepared.KeyId.Value.Value:N}:{prepared.KeyFingerprint}";
            keyPath = TryLeaseCached(cacheKey);
            if (keyPath is null)
            {
                var privateKey = await service.RevealPrivateKeyAsync(prepared, cancellationToken);
                keyPath = LeaseNew(cacheKey, _files.WritePrivateKey(privateKey));
            }
        }

        try
        {
            SshHostIdentityPolicy policy;
            string? knownHosts = null;
            if (prepared.Mode == SshHostKeyCheckingMode.Off || prepared.PinnedHostKeys is null)
            {
                // Off, or warn mode for a server whose key could not be read yet (enforce never gets here unpinned).
                policy = SshHostIdentityPolicy.InsecureSkipValidation;
            }
            else
            {
                knownHosts = _files.WriteKnownHosts(prepared.ServerId,
                    SshKeyFormats.KnownHostsContent(prepared.HostKeyAlias, prepared.PinnedHostKeys));
                policy = prepared.Mode == SshHostKeyCheckingMode.Enforce
                    ? SshHostIdentityPolicy.RequireKnownHost
                    : SshHostIdentityPolicy.WarnOnMismatch;
            }

            var options = new SshConnectionOptions(
                prepared.Host,
                prepared.Port,
                prepared.User,
                keyPath,
                HostIdentityPolicy: policy,
                ConnectTimeout: TimeSpan.FromSeconds(Math.Max(1, _options.Value.ConnectTimeoutSeconds)),
                KnownHostsPath: knownHosts,
                HostKeyAlias: prepared.HostKeyAlias,
                ServerAliveInterval: TimeSpan.FromSeconds(Math.Max(1, _options.Value.ServerAliveIntervalSeconds)));
            return new SshConnectionLease(options, () =>
            {
                if (cacheKey is not null) Release(cacheKey);
                return ValueTask.CompletedTask;
            });
        }
        catch
        {
            if (cacheKey is not null) Release(cacheKey);
            throw;
        }
    }

    public async Task ObserveAsync(ServerExecutionTarget target, string? standardError,
        CancellationToken cancellationToken = default)
    {
        if (target is null || target.ConnectionType != ServerConnectionType.Ssh ||
            !SshKeyFormats.IsHostKeyMismatch(standardError))
            return;
        try
        {
            await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ServerSshConnectionService>();
            await service.RecordHostKeyMismatchAsync(target.ServerId, standardError, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not record the changed SSH host key of server {ServerId}.", target.ServerId);
        }
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        lock (_gate)
        {
            foreach (CachedKeyFile entry in _keys.Values)
                _files.Delete(entry.Path);
            _keys.Clear();
        }
    }

    private string? TryLeaseCached(string cacheKey)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue(cacheKey, out CachedKeyFile? entry)) return null;
            if (!File.Exists(entry.Path))
            {
                if (entry.Leases == 0) _keys.Remove(cacheKey);
                return null;
            }

            entry.Leases++;
            return entry.Path;
        }
    }

    private string LeaseNew(string cacheKey, string path)
    {
        lock (_gate)
        {
            if (_keys.TryGetValue(cacheKey, out CachedKeyFile? existing) && File.Exists(existing.Path))
            {
                // Another connection wrote the same key meanwhile: use it, drop ours.
                existing.Leases++;
                _files.Delete(path);
                return existing.Path;
            }

            _keys[cacheKey] = new CachedKeyFile(path) { Leases = 1, LastUsed = _timeProvider.GetUtcNow() };
            return path;
        }
    }

    private void Release(string cacheKey)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue(cacheKey, out CachedKeyFile? entry)) return;
            entry.Leases = Math.Max(0, entry.Leases - 1);
            entry.LastUsed = _timeProvider.GetUtcNow();
        }
    }

    private void Sweep()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            foreach (var (key, entry) in _keys.ToArray())
            {
                if (entry.Leases > 0 || now - entry.LastUsed < IdleKeyLifetime) continue;
                _files.Delete(entry.Path);
                _keys.Remove(key);
            }
        }
    }

    private sealed class CachedKeyFile(string path)
    {
        public string Path { get; } = path;

        public int Leases { get; set; }

        public DateTimeOffset LastUsed { get; set; }
    }
}
