namespace Vessel.Domain.Common;

internal static class DomainValidation
{
    public static string Required(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DomainException($"{name} is required.");

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw new DomainException($"{name} must be {maxLength} characters or fewer.");

        return normalized;
    }

    public static string? Optional(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw new DomainException($"{name} must be {maxLength} characters or fewer.");

        return normalized;
    }
}
