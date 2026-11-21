namespace Vessel.Domain.Servers;

/// <summary>
///     How Vessel verifies the SSH host key of a remote server. The first connection pins the host key (trust on
///     first use). <see cref="Enforce" /> refuses a changed key, <see cref="Warn" /> connects and reports the change,
///     <see cref="Off" /> skips the check. Values are ordered by strictness.
/// </summary>
public enum SshHostKeyCheckingMode
{
    Off = 0,
    Warn = 1,
    Enforce = 2
}
