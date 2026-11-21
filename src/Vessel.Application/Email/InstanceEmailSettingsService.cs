using System.Text.Json.Serialization;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.OutboundRequests;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Settings;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Email;

/// <summary>
///     Instance email settings (upstream <c>GET/PATCH /settings/email</c>). Instance administrators only. Secrets are
///     write-only: responses carry <c>*_set</c> flags, never passwords or API keys. Field names follow upstream Coolify
///     v4.7.0, including <c>resend_from_address</c>/<c>resend_from_name</c>.
/// </summary>
public sealed class InstanceEmailSettingsService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    ISecretCipher cipher,
    IOutboundUrlPolicy outboundUrlPolicy,
    InstanceEmailSender sender,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public InstanceEmailSettingsView Get(UserId actorUserId)
    {
        RequireInstanceAdmin(actorUserId);
        return ToView(dbContext.InstanceEmailSettings.FirstOrDefault());
    }

    public IReadOnlyList<SmtpPresetView> ListPresets(UserId actorUserId)
    {
        RequireInstanceAdmin(actorUserId);
        return SmtpPresets.All
            .Select(preset => new SmtpPresetView(preset.Key, preset.Label, preset.Host, preset.Port,
                SmtpEncryptionNames.ToName(preset.Encryption), preset.Username))
            .ToArray();
    }

    public async Task<InstanceEmailSettingsView> UpdateAsync(
        UserId actorUserId,
        UpdateInstanceEmailSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireInstanceAdmin(actorUserId);
        ArgumentNullException.ThrowIfNull(request);

        var now = timeProvider.GetUtcNow();
        var settings = dbContext.InstanceEmailSettings.FirstOrDefault();
        var isNew = settings is null;
        settings ??= InstanceEmailSettings.Create(now);

        var provider = request.Provider is null ? settings.Provider : ParseProvider(request.Provider);
        var servers = request.SmtpServers is null
            ? settings.SmtpServers
            : await BuildServersAsync(settings, request.SmtpServers, cancellationToken);
        var protectedResendKey = request.ResendApiKey switch
        {
            null => settings.ProtectedResendApiKey,
            "" => null,
            var key => cipher.Protect(key.Trim(), InstanceEmailSender.ResendApiKeyPurpose)
        };

        settings.Update(
            provider,
            request.ActiveSmtpServer ?? (servers.Any(server =>
                string.Equals(server.Name, settings.ActiveSmtpServerName, StringComparison.Ordinal))
                ? settings.ActiveSmtpServerName
                : null),
            servers,
            protectedResendKey,
            Patch(request.ResendFromAddress, settings.ResendFromAddress),
            Patch(request.ResendFromName, settings.ResendFromName),
            Patch(request.NotificationFromAddress, settings.NotificationFromAddress),
            Patch(request.NotificationFromName, settings.NotificationFromName),
            now);

        if (isNew)
            await dbContext.InstanceEmailSettingsRepository.AddAsync(settings, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.RecordAsync(null, actorUserId, AuditActions.InstanceEmailSettingsUpdated,
            new AuditTarget("instance_email_settings", settings.Id.ToString()), null,
            new Dictionary<string, object?>
            {
                ["provider"] = settings.Provider.ToString(),
                ["smtpServers"] = settings.SmtpServers.Count,
                ["changedFields"] = ChangedFields(request)
            }, cancellationToken);

        return ToView(settings);
    }

    /// <summary>Sends a fixed test email through the saved settings and reports the safe outcome.</summary>
    public async Task<EmailSendResult> SendTestAsync(UserId actorUserId, string recipient,
        CancellationToken cancellationToken = default)
    {
        RequireInstanceAdmin(actorUserId);
        var to = new EmailAddress(recipient).Value;
        var result = await sender.SendAsync(new EmailMessage([to], "Vessel test email",
                "This is a test email from Vessel. If you can read this, the instance email settings work."),
            EmailPurpose.Transactional, cancellationToken);

        await auditWriter.RecordAsync(null, actorUserId, AuditActions.InstanceEmailTestSent,
            new AuditTarget("instance_email_settings", InstanceEmailSettingsId.Singleton.ToString()), null,
            new Dictionary<string, object?> { ["success"] = result.Success }, cancellationToken);
        return result;
    }

    private async Task<IReadOnlyList<SmtpServerSettings>> BuildServersAsync(InstanceEmailSettings existing,
        IReadOnlyList<SmtpServerInput> inputs, CancellationToken cancellationToken)
    {
        var servers = new List<SmtpServerSettings>(inputs.Count);
        foreach (var input in inputs)
        {
            var preset = string.IsNullOrWhiteSpace(input.Preset)
                ? null
                : SmtpPresets.All.FirstOrDefault(candidate =>
                      string.Equals(candidate.Key, input.Preset.Trim(), StringComparison.OrdinalIgnoreCase))
                  ?? throw new DomainException("Unknown SMTP preset.");

            var name = string.IsNullOrWhiteSpace(input.Name) ? InstanceEmailSettings.DefaultSmtpServerName : input.Name;
            var previous = existing.SmtpServers.FirstOrDefault(server =>
                string.Equals(server.Name, name.Trim(), StringComparison.Ordinal));
            var host = input.Host ?? preset?.Host ?? previous?.Host
                ?? throw new DomainException("The SMTP host is required.");

            // SEC-08: refuse loopback, link-local/metadata, reserved, and (unless allow-listed) private targets when
            // saving. The transport checks again and pins the connection when sending.
            var normalizedHost = SmtpServerSettings.NormalizeHost(host);
            if (!SmtpServerSettings.IsValidHost(normalizedHost))
                throw new DomainException("The SMTP host must be a valid hostname or IP address.");
            var check = await outboundUrlPolicy.CheckHostAsync(normalizedHost, cancellationToken);
            if (!check.Allowed)
                throw new DomainException($"The SMTP host is not allowed: {check.Reason}");

            var protectedPassword = input.Password switch
            {
                null => previous?.ProtectedPassword,
                "" => null,
                var password => cipher.Protect(password, InstanceEmailSender.SmtpPasswordPurpose)
            };

            servers.Add(SmtpServerSettings.Create(
                name,
                normalizedHost,
                input.Port ?? preset?.Port ?? previous?.Port ?? 587,
                input.Encryption is not null
                    ? SmtpEncryptionNames.Parse(input.Encryption)
                    : preset?.Encryption ?? previous?.Encryption ?? SmtpEncryption.StartTls,
                input.Username ?? preset?.Username ?? previous?.Username,
                protectedPassword,
                input.FromAddress ?? previous?.FromAddress ?? throw new DomainException(
                    "The SMTP sender address (smtp_from_address) is required."),
                input.FromName ?? previous?.FromName,
                input.EhloDomain ?? previous?.EhloDomain,
                input.TimeoutSeconds ?? previous?.TimeoutSeconds));
        }

        return servers;
    }

    private void RequireInstanceAdmin(UserId actorUserId)
    {
        if (!authorization.IsInstanceAdmin(actorUserId))
            throw new UnauthorizedAccessException("Instance email settings require an instance administrator.");
    }

    private static EmailProvider ParseProvider(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "none" or "" => EmailProvider.None,
            "smtp" => EmailProvider.Smtp,
            "resend" => EmailProvider.Resend,
            _ => throw new DomainException("The email provider must be one of none, smtp, or resend.")
        };
    }

    private static string? Patch(string? requested, string? current)
    {
        return requested is null ? current : string.IsNullOrWhiteSpace(requested) ? null : requested;
    }

    private static string[] ChangedFields(UpdateInstanceEmailSettingsRequest request)
    {
        var fields = new List<string>();
        if (request.Provider is not null) fields.Add("provider");
        if (request.ActiveSmtpServer is not null) fields.Add("smtp_server_name");
        if (request.SmtpServers is not null) fields.Add("smtp_servers");
        if (request.ResendApiKey is not null) fields.Add("resend_api_key");
        if (request.ResendFromAddress is not null) fields.Add("resend_from_address");
        if (request.ResendFromName is not null) fields.Add("resend_from_name");
        if (request.NotificationFromAddress is not null) fields.Add("notification_from_address");
        if (request.NotificationFromName is not null) fields.Add("notification_from_name");
        return fields.ToArray();
    }

    private static InstanceEmailSettingsView ToView(InstanceEmailSettings? settings)
    {
        if (settings is null)
            return new InstanceEmailSettingsView("none", false, null, [], false, null, null, null, null);

        return new InstanceEmailSettingsView(
            settings.Provider.ToString().ToLowerInvariant(),
            settings.IsEnabled,
            settings.ActiveSmtpServerName,
            settings.SmtpServers.Select(server => new SmtpServerView(server.Name, server.Host, server.Port,
                SmtpEncryptionNames.ToName(server.Encryption), server.Username, server.ProtectedPassword is not null,
                server.FromAddress, server.FromName, server.EhloDomain, server.TimeoutSeconds)).ToArray(),
            settings.ProtectedResendApiKey is not null,
            settings.ResendFromAddress,
            settings.ResendFromName,
            settings.NotificationFromAddress,
            settings.NotificationFromName);
    }
}

public sealed record InstanceEmailSettingsView(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("smtp_server_name")] string? ActiveSmtpServer,
    [property: JsonPropertyName("smtp_servers")] IReadOnlyList<SmtpServerView> SmtpServers,
    [property: JsonPropertyName("resend_api_key_set")] bool ResendApiKeySet,
    [property: JsonPropertyName("resend_from_address")] string? ResendFromAddress,
    [property: JsonPropertyName("resend_from_name")] string? ResendFromName,
    [property: JsonPropertyName("notification_from_address")] string? NotificationFromAddress,
    [property: JsonPropertyName("notification_from_name")] string? NotificationFromName);

public sealed record SmtpServerView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("smtp_host")] string Host,
    [property: JsonPropertyName("smtp_port")] int Port,
    [property: JsonPropertyName("smtp_encryption")] string Encryption,
    [property: JsonPropertyName("smtp_username")] string? Username,
    [property: JsonPropertyName("smtp_password_set")] bool PasswordSet,
    [property: JsonPropertyName("smtp_from_address")] string FromAddress,
    [property: JsonPropertyName("smtp_from_name")] string? FromName,
    [property: JsonPropertyName("smtp_ehlo_domain")] string? EhloDomain,
    [property: JsonPropertyName("smtp_timeout")] int TimeoutSeconds);

public sealed record SmtpPresetView(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("smtp_host")] string Host,
    [property: JsonPropertyName("smtp_port")] int Port,
    [property: JsonPropertyName("smtp_encryption")] string Encryption,
    [property: JsonPropertyName("smtp_username")] string? Username);

/// <summary>
///     PATCH body. Omitted (null) fields keep their value; an empty string clears an optional field or secret.
///     <see cref="SmtpServers" />, when present, replaces the saved list (a server's password is kept when its
///     <c>smtp_password</c> is omitted and the name is unchanged).
/// </summary>
public sealed record UpdateInstanceEmailSettingsRequest(
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("smtp_server_name")] string? ActiveSmtpServer = null,
    [property: JsonPropertyName("smtp_servers")] IReadOnlyList<SmtpServerInput>? SmtpServers = null,
    [property: JsonPropertyName("resend_api_key")] string? ResendApiKey = null,
    [property: JsonPropertyName("resend_from_address")] string? ResendFromAddress = null,
    [property: JsonPropertyName("resend_from_name")] string? ResendFromName = null,
    [property: JsonPropertyName("notification_from_address")] string? NotificationFromAddress = null,
    [property: JsonPropertyName("notification_from_name")] string? NotificationFromName = null)
{
    public override string ToString()
    {
        return "UpdateInstanceEmailSettingsRequest { [redacted] }";
    }
}

public sealed record SmtpServerInput(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("preset")] string? Preset = null,
    [property: JsonPropertyName("smtp_host")] string? Host = null,
    [property: JsonPropertyName("smtp_port")] int? Port = null,
    [property: JsonPropertyName("smtp_encryption")] string? Encryption = null,
    [property: JsonPropertyName("smtp_username")] string? Username = null,
    [property: JsonPropertyName("smtp_password")] string? Password = null,
    [property: JsonPropertyName("smtp_from_address")] string? FromAddress = null,
    [property: JsonPropertyName("smtp_from_name")] string? FromName = null,
    [property: JsonPropertyName("smtp_ehlo_domain")] string? EhloDomain = null,
    [property: JsonPropertyName("smtp_timeout")] int? TimeoutSeconds = null)
{
    public override string ToString()
    {
        return $"SmtpServerInput {{ Name = {Name}, Host = {Host} }}";
    }
}

public sealed record SendTestEmailRequest([property: JsonPropertyName("to")] string To);
