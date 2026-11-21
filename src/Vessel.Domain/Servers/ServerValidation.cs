namespace Vessel.Domain.Servers;

/// <summary>
///     Result of the last server validation: detected operating system, container runtime and Compose versions, the
///     login user's privileges, and the outcome. Stored with the server so operators can see why it is (not) usable.
/// </summary>
public sealed record ServerValidation(
    bool Succeeded,
    string? OsId,
    string? OsIdLike,
    string? OsVersion,
    string? PackageManager,
    string? RuntimeVersion,
    string? ComposeVersion,
    bool IsRootUser,
    bool HasPasswordlessSudo,
    string? Message,
    DateTimeOffset ValidatedAt)
{
    public const int MaxMessageLength = 1000;
    public const int MaxFieldLength = 128;

    public static ServerValidation Create(
        bool succeeded,
        string? osId,
        string? osIdLike,
        string? osVersion,
        string? packageManager,
        string? runtimeVersion,
        string? composeVersion,
        bool isRootUser,
        bool hasPasswordlessSudo,
        string? message,
        DateTimeOffset validatedAt)
    {
        return new ServerValidation(
            succeeded,
            Clip(osId, MaxFieldLength),
            Clip(osIdLike, MaxFieldLength),
            Clip(osVersion, MaxFieldLength),
            Clip(packageManager, MaxFieldLength),
            Clip(runtimeVersion, MaxFieldLength),
            Clip(composeVersion, MaxFieldLength),
            isRootUser,
            hasPasswordlessSudo,
            Clip(message, MaxMessageLength),
            validatedAt);
    }

    private static string? Clip(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
