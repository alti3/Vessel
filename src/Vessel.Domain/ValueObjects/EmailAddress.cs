using System.Net.Mail;
using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

public readonly record struct EmailAddress
{
    public const int MaxLength = 320;

    public EmailAddress(string value)
    {
        var normalized = DomainValidation.Required(value, nameof(EmailAddress), MaxLength).ToLowerInvariant();

        try
        {
            _ = new MailAddress(normalized);
        }
        catch (FormatException exception)
        {
            throw new DomainException("Email address is invalid.", exception);
        }

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
