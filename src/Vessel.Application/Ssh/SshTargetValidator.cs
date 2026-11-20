using System.Net;
using System.Text.RegularExpressions;

namespace Vessel.Application.Ssh;

/// <summary>
/// Conservative validation for SSH destinations taken from persisted server records. Rejects values that
/// could be parsed as ssh options (leading '-') or carry whitespace/control characters.
/// </summary>
public static partial class SshTargetValidator
{
    public static bool IsSafeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.StartsWith('-')) return false;
        if (IPAddress.TryParse(host, out _)) return true;
        return HostNamePattern().IsMatch(host);
    }

    public static bool IsSafeUser(string? user)
    {
        return !string.IsNullOrWhiteSpace(user) && UserPattern().IsMatch(user);
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HostNamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserPattern();
}
