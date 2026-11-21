using System.Globalization;

namespace Vessel.Domain.Operations;

/// <summary>
///     A Vessel release version in semantic-version form (<c>1.4.0</c>, <c>v1.5.0-beta.2</c>,
///     <c>1.5.0-nightly.20261121+abc123</c>). Comparison follows SemVer 2.0 precedence: build metadata is ignored, a
///     pre-release sorts before its release, numeric pre-release identifiers compare numerically.
/// </summary>
public sealed class VesselVersion : IComparable<VesselVersion>, IEquatable<VesselVersion>
{
    private VesselVersion(int major, int minor, int patch, string[] preRelease, string? build)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
        Build = build;
    }

    public static VesselVersion Zero { get; } = new(0, 0, 0, [], null);

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public IReadOnlyList<string> PreRelease { get; }

    public string? Build { get; }

    public bool IsPreRelease => PreRelease.Count > 0;

    /// <summary>The release this version leads to (<c>1.5.0</c> for <c>1.5.0-beta.2</c>).</summary>
    public VesselVersion Core => new(Major, Minor, Patch, [], null);

    public static bool TryParse(string? value, out VesselVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        string? build = null;
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            build = text[(plus + 1)..];
            text = text[..plus];
            if (build.Length == 0 || !build.Split('.').All(IsIdentifier)) return false;
        }

        string[] pre = [];
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = text[(dash + 1)..].Split('.');
            text = text[..dash];
            if (pre.Any(identifier => !IsIdentifier(identifier))) return false;
        }

        var parts = text.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
            if (parts[i].Length == 0 || parts[i].Length > 9 || !parts[i].All(char.IsAsciiDigit) ||
                !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;

        version = new VesselVersion(numbers[0], numbers[1], numbers[2], pre, build);
        return true;
    }

    public int CompareTo(VesselVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (PreRelease.Count == 0 || other.PreRelease.Count == 0)
            return other.PreRelease.Count.CompareTo(PreRelease.Count) switch { > 0 => 1, < 0 => -1, _ => 0 };
        for (var i = 0; i < Math.Min(PreRelease.Count, other.PreRelease.Count); i++)
        {
            result = CompareIdentifier(PreRelease[i], other.PreRelease[i]);
            if (result != 0) return result;
        }

        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    public bool Equals(VesselVersion? other)
    {
        return other is not null && CompareTo(other) == 0;
    }

    public override bool Equals(object? obj)
    {
        return obj is VesselVersion other && Equals(other);
    }

    public static bool operator ==(VesselVersion? left, VesselVersion? right)
    {
        return left is null ? right is null : left.Equals(right);
    }

    public static bool operator !=(VesselVersion? left, VesselVersion? right)
    {
        return !(left == right);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Major, Minor, Patch, string.Join('.', PreRelease));
    }

    public override string ToString()
    {
        var text = $"{Major}.{Minor}.{Patch}";
        if (PreRelease.Count > 0) text += "-" + string.Join('.', PreRelease);
        if (Build is not null) text += "+" + Build;
        return text;
    }

    public static bool operator <(VesselVersion left, VesselVersion right)
    {
        return left.CompareTo(right) < 0;
    }

    public static bool operator >(VesselVersion left, VesselVersion right)
    {
        return left.CompareTo(right) > 0;
    }

    public static bool operator <=(VesselVersion left, VesselVersion right)
    {
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >=(VesselVersion left, VesselVersion right)
    {
        return left.CompareTo(right) >= 0;
    }

    private static bool IsIdentifier(string identifier)
    {
        return identifier.Length is > 0 and <= 64 && identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            var byLength = left.TrimStart('0').Length.CompareTo(right.TrimStart('0').Length);
            return byLength != 0
                ? byLength
                : string.CompareOrdinal(left.TrimStart('0'), right.TrimStart('0'));
        }

        if (leftNumeric) return -1;
        if (rightNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }
}
