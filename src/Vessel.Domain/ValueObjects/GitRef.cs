using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

/// <summary>
///     A validated git reference (branch, tag, commit SHA or provider ref such as <c>refs/pull/7/head</c>) that is safe to
///     pass as a positional git argument. Mirrors Coolify's <c>validateGitRef</c> allowlist and additionally rejects
///     sequences git itself refuses (<c>..</c>, <c>//</c>, <c>.lock</c> suffix), so a ref can never be read as an option.
/// </summary>
public readonly partial record struct GitRef
{
    public const int MaxLength = 255;

    private GitRef(string value)
    {
        Value = value;
    }

    public string Value { get; }

    /// <summary>True for a full or abbreviated hexadecimal commit object name (7 to 64 characters).</summary>
    public bool IsCommitSha => IsCommitShaValue(Value);

    /// <summary>True for a full SHA-1 (40) or SHA-256 (64) object name that servers can fetch directly.</summary>
    public bool IsFullCommitSha => IsCommitSha && Value.Length is 40 or 64;

    public static GitRef Parse(string? value, string context = "git ref")
    {
        return TryParse(value, out GitRef gitRef, out var error)
            ? gitRef
            : throw new DomainException($"Invalid {context}: {error}");
    }

    public static bool TryParse(string? value, out GitRef gitRef)
    {
        return TryParse(value, out gitRef, out _);
    }

    public static bool TryParse(string? value, out GitRef gitRef, out string error)
    {
        gitRef = default;
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            error = "value is required.";
            return false;
        }

        if (trimmed.Length > MaxLength)
        {
            error = $"must be {MaxLength} characters or fewer.";
            return false;
        }

        if (trimmed.StartsWith('-'))
        {
            error = "must not start with a hyphen.";
            return false;
        }

        if (!AllowedPattern().IsMatch(trimmed))
        {
            error = "only letters, digits, '.', '_', '-' and '/' are allowed and it must start with a letter or digit.";
            return false;
        }

        if (trimmed.Contains("..", StringComparison.Ordinal) || trimmed.Contains("//", StringComparison.Ordinal) ||
            trimmed.EndsWith('/') || trimmed.EndsWith('.') ||
            trimmed.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("/.", StringComparison.Ordinal))
        {
            error = "contains a sequence git does not allow in reference names.";
            return false;
        }

        gitRef = new GitRef(trimmed);
        error = string.Empty;
        return true;
    }

    public static bool IsCommitShaValue(string? value)
    {
        return value is { Length: >= 7 and <= 64 } && HexPattern().IsMatch(value);
    }

    /// <summary>All-zero object name that providers send for branch deletion pushes.</summary>
    public static bool IsZeroSha(string? value)
    {
        return value is { Length: 40 or 64 } && value.All(character => character == '0');
    }

    public override string ToString()
    {
        return Value;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedPattern();

    [GeneratedRegex("^[0-9a-fA-F]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HexPattern();
}
