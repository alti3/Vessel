using System.Net;
using System.Text.RegularExpressions;
using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Settings;

/// <summary>Instance mail provider. Providers are mutually exclusive (upstream v4.4.0/v4.7.0).</summary>
public enum EmailProvider
{
    None = 0,
    Smtp = 1,
    Resend = 2
}

/// <summary>SMTP transport security. <see cref="Tls" /> is implicit TLS (SMTPS, usually port 465).</summary>
public enum SmtpEncryption
{
    None = 0,
    StartTls = 1,
    Tls = 2
}

/// <summary>
///     One named SMTP server with its own sender, because each server usually sends from its own verified domain
///     (upstream <c>HasSmtpServers</c>). <see cref="ProtectedPassword" /> is an opaque envelope produced by the
///     Application secret cipher; the domain never sees the plaintext.
/// </summary>
public sealed partial record SmtpServerSettings(
    string Name,
    string Host,
    int Port,
    SmtpEncryption Encryption,
    string? Username,
    string? ProtectedPassword,
    string FromAddress,
    string? FromName,
    string? EhloDomain,
    int TimeoutSeconds)
{
    public const int DefaultTimeoutSeconds = 30;
    public const int MaxNameLength = 80;

    public static SmtpServerSettings Create(
        string name,
        string host,
        int port,
        SmtpEncryption encryption,
        string? username,
        string? protectedPassword,
        string fromAddress,
        string? fromName,
        string? ehloDomain,
        int? timeoutSeconds)
    {
        var normalizedHost = NormalizeHost(host);
        if (!IsValidHost(normalizedHost))
            throw new DomainException("The SMTP host must be a valid hostname or IP address.");
        if (port is < 1 or > 65535)
            throw new DomainException("The SMTP port must be between 1 and 65535.");
        if (!Enum.IsDefined(encryption))
            throw new DomainException("The SMTP encryption mode is not supported.");

        var ehlo = DomainValidation.Optional(ehloDomain, "SMTP EHLO domain", 253);
        if (ehlo is not null)
        {
            ehlo = NormalizeHost(ehlo);
            if (!IsValidHost(ehlo))
                throw new DomainException("The SMTP EHLO domain must be a valid hostname.");
        }

        var timeout = timeoutSeconds ?? DefaultTimeoutSeconds;
        if (timeout is < 1 or > 300)
            throw new DomainException("The SMTP timeout must be between 1 and 300 seconds.");

        return new SmtpServerSettings(
            DomainValidation.Required(name, "SMTP server name", MaxNameLength),
            normalizedHost,
            port,
            encryption,
            DomainValidation.Optional(username, "SMTP username", 255),
            string.IsNullOrWhiteSpace(protectedPassword) ? null : protectedPassword,
            new EmailAddress(fromAddress).Value,
            SenderName(fromName),
            ehlo,
            timeout);
    }

    /// <summary>
    ///     Lower-case host without brackets or the trailing dot of a fully qualified name. TLS verifies this name.
    /// </summary>
    public static string NormalizeHost(string? host)
    {
        var value = (host ?? string.Empty).Trim().Trim('[', ']');
        if (IPAddress.TryParse(value, out var address))
            return address.ToString();

        value = value.ToLowerInvariant();
        return value.EndsWith('.') ? value[..^1] : value;
    }

    /// <summary>An IP address, or a host name of letters, digits, hyphens, and underscores (upstream SafeSmtpHost).</summary>
    public static bool IsValidHost(string host)
    {
        if (IPAddress.TryParse(host, out _))
            return true;
        if (string.IsNullOrEmpty(host) || host.Length > 253)
            return false;

        return host.Split('.').All(label => HostLabel().IsMatch(label));
    }

    internal static string? SenderName(string? value)
    {
        var name = DomainValidation.Optional(value, "Sender name", 255);
        if (name is not null && name.Any(char.IsControl))
            throw new DomainException("Sender name must not contain control characters.");
        return name;
    }

    [GeneratedRegex("^[a-z0-9_](?:[a-z0-9_-]{0,61}[a-z0-9_])?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HostLabel();
}

/// <summary>
///     Instance-level transactional email settings (NTF-06 / IAM-10), administered by instance administrators only.
///     They deliver password reset, team invitation, and email verification mail, and can serve notification email
///     targets that choose "use system settings". Secrets (SMTP passwords, the Resend API key) are stored as opaque
///     envelopes protected by the Application secret cipher.
/// </summary>
public sealed class InstanceEmailSettings : Entity<InstanceEmailSettingsId>
{
    public const string DefaultSmtpServerName = "Default";
    public const int MaxSmtpServers = 20;

    private InstanceEmailSettings()
    {
    }

    private InstanceEmailSettings(DateTimeOffset now)
        : base(InstanceEmailSettingsId.Singleton, now)
    {
        Provider = EmailProvider.None;
    }

    public EmailProvider Provider { get; private set; }

    /// <summary>Name of the SMTP server in use when <see cref="Provider" /> is <see cref="EmailProvider.Smtp" />.</summary>
    public string? ActiveSmtpServerName { get; private set; }

    public IReadOnlyList<SmtpServerSettings> SmtpServers { get; private set; } = [];

    public string? ProtectedResendApiKey { get; private set; }

    /// <summary>Resend sender (v4.7.0 <c>resend_from_address</c>).</summary>
    public string? ResendFromAddress { get; private set; }

    /// <summary>Resend sender name (v4.7.0 <c>resend_from_name</c>).</summary>
    public string? ResendFromName { get; private set; }

    /// <summary>
    ///     Sender for team notifications and channel-disabled alerts (v4.7.0 <c>notification_from_address</c>). Falls
    ///     back to the transactional sender of the active provider.
    /// </summary>
    public string? NotificationFromAddress { get; private set; }

    public string? NotificationFromName { get; private set; }

    public SmtpServerSettings? ActiveSmtpServer =>
        SmtpServers.FirstOrDefault(server => string.Equals(server.Name, ActiveSmtpServerName, StringComparison.Ordinal));

    /// <summary>True when the active provider has everything it needs to send.</summary>
    public bool IsEnabled => Provider switch
    {
        EmailProvider.Smtp => ActiveSmtpServer is not null,
        EmailProvider.Resend => ProtectedResendApiKey is not null && ResendFromAddress is not null,
        _ => false
    };

    public static InstanceEmailSettings Create(DateTimeOffset now)
    {
        return new InstanceEmailSettings(now);
    }

    public void Update(
        EmailProvider provider,
        string? activeSmtpServerName,
        IReadOnlyList<SmtpServerSettings> smtpServers,
        string? protectedResendApiKey,
        string? resendFromAddress,
        string? resendFromName,
        string? notificationFromAddress,
        string? notificationFromName,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(smtpServers);
        if (!Enum.IsDefined(provider))
            throw new DomainException("The email provider is not supported.");
        if (smtpServers.Count > MaxSmtpServers)
            throw new DomainException($"At most {MaxSmtpServers} SMTP servers can be saved.");
        if (smtpServers.Select(server => server.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            smtpServers.Count)
            throw new DomainException("SMTP server names must be unique.");

        var active = string.IsNullOrWhiteSpace(activeSmtpServerName)
            ? (smtpServers.Count > 0 ? smtpServers[0].Name : null)
            : activeSmtpServerName.Trim();
        if (active is not null && smtpServers.Count > 0 &&
            !smtpServers.Any(server => string.Equals(server.Name, active, StringComparison.Ordinal)))
            throw new DomainException("The active SMTP server must be one of the saved servers.");

        var resendFrom = string.IsNullOrWhiteSpace(resendFromAddress) ? null : new EmailAddress(resendFromAddress).Value;
        var notificationFrom = string.IsNullOrWhiteSpace(notificationFromAddress)
            ? null
            : new EmailAddress(notificationFromAddress).Value;

        switch (provider)
        {
            case EmailProvider.Smtp when smtpServers.Count == 0:
                throw new DomainException("SMTP needs at least one SMTP server.");
            case EmailProvider.Resend when string.IsNullOrWhiteSpace(protectedResendApiKey):
                throw new DomainException("Resend needs an API key.");
            case EmailProvider.Resend when resendFrom is null:
                throw new DomainException("Resend needs a sender address (resend_from_address).");
        }

        Provider = provider;
        SmtpServers = smtpServers.ToArray();
        ActiveSmtpServerName = smtpServers.Count == 0 ? null : active;
        ProtectedResendApiKey = string.IsNullOrWhiteSpace(protectedResendApiKey) ? null : protectedResendApiKey;
        ResendFromAddress = resendFrom;
        ResendFromName = SmtpServerSettings.SenderName(resendFromName);
        NotificationFromAddress = notificationFrom;
        NotificationFromName = SmtpServerSettings.SenderName(notificationFromName);
        Touch(now);
    }
}
