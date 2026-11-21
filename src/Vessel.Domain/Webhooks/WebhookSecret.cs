using Vessel.Domain.Common;

namespace Vessel.Domain.Webhooks;

/// <summary>
///     A manual webhook secret (HMAC key or static token) for a new or changed configuration. Coolify parity
///     (v4.4.0): at least 16 characters. Existing stored secrets are not re-validated, so older configurations keep
///     working until their secret is changed.
/// </summary>
public readonly record struct WebhookSecret
{
    public const int MinLength = 16;
    public const int MaxLength = 512;

    public WebhookSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DomainException("Webhook secret is required.");
        if (value.Length < MinLength)
            throw new DomainException($"Webhook secret must be at least {MinLength} characters.");
        if (value.Length > MaxLength)
            throw new DomainException($"Webhook secret cannot exceed {MaxLength} characters.");

        Value = value;
    }

    public string Value { get; }

    /// <summary>Never renders the secret itself.</summary>
    public override string ToString()
    {
        return "[redacted]";
    }
}
