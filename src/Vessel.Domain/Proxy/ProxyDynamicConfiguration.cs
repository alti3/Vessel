using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.Proxy;

/// <summary>
///     A user-managed Traefik dynamic configuration file delivered next to Vessel's generated routes (upstream
///     <c>DynamicConfigurations</c>). <see cref="Content" /> is stored protected (encrypted) like configuration versions.
/// </summary>
public sealed partial class ProxyDynamicConfiguration : Entity<ProxyDynamicConfigurationId>
{
    public const int MaxContentLength = ProxyConfigurationVersion.MaxConfigurationLength;

    private ProxyDynamicConfiguration()
    {
    }

    private ProxyDynamicConfiguration(
        ProxyDynamicConfigurationId id,
        ServerId serverId,
        string fileName,
        string protectedContent,
        string contentHash,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        ServerId = serverId;
        FileName = ValidateFileName(fileName);
        Replace(protectedContent, contentHash, createdAt);
    }

    public ServerId ServerId { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public string Content { get; private set; } = string.Empty;

    public string ContentHash { get; private set; } = string.Empty;

    public static ProxyDynamicConfiguration Create(
        ServerId serverId,
        string fileName,
        string protectedContent,
        string contentHash,
        DateTimeOffset now)
    {
        return new ProxyDynamicConfiguration(ProxyDynamicConfigurationId.New(), serverId, fileName, protectedContent,
            contentHash, now);
    }

    public void Replace(string protectedContent, string contentHash, DateTimeOffset now)
    {
        Content = DomainValidation.Required(protectedContent, nameof(Content), MaxContentLength);
        ContentHash = DomainValidation.Required(contentHash, nameof(ContentHash), 128);
        Touch(now);
    }

    /// <summary>
    ///     File-name rule (upstream <c>ValidProxyConfigFilename</c>, tightened): a plain <c>.yml</c>/<c>.yaml</c> name of
    ///     letters, digits, dots, dashes and underscores, no path separators, not hidden, and not in the reserved
    ///     <c>vessel</c> namespace (Vessel's generated routes live there).
    /// </summary>
    public static string ValidateFileName(string fileName)
    {
        var name = DomainValidation.Required(fileName, nameof(FileName), ProxyConfigurationVersion.MaxFileNameLength);
        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
            throw new DomainException("Dynamic configuration file name cannot contain path separators.");
        if (name.StartsWith('.'))
            throw new DomainException("Dynamic configuration file name cannot start with a dot.");
        if (!FileNamePattern().IsMatch(name))
            throw new DomainException(
                "Dynamic configuration file name may only contain letters, digits, '.', '-' and '_' and must end in .yml or .yaml.");
        if (name.StartsWith("vessel", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Dynamic configuration file names starting with 'vessel' are reserved.");
        return name;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._-]*\.ya?ml$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
}
