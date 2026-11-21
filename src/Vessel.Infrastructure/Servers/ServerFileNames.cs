using Vessel.Application.Servers.Files;

namespace Vessel.Infrastructure.Servers;

/// <summary>Filters directory listings to names that are valid, non-hidden server file path segments.</summary>
internal static class ServerFileNames
{
    public static IReadOnlyList<string> Filter(IEnumerable<string> names)
    {
        var accepted = new List<string>();
        foreach (var raw in names)
        {
            var name = raw.TrimEnd('\r');
            if (name.Length == 0 || name.StartsWith('.') || name.Contains('/', StringComparison.Ordinal)) continue;
            try
            {
                accepted.Add(ServerFilePaths.ValidateRelative(name));
            }
            catch (ArgumentException)
            {
                // Names Vessel would never write (spaces, shell metacharacters) are ignored.
            }
        }

        accepted.Sort(StringComparer.Ordinal);
        return accepted;
    }
}
