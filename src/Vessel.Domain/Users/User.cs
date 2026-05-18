using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Users;

public sealed class User : Entity<UserId>
{
    private User()
    {
    }

    private User(UserId id, DisplayName name, EmailAddress email, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        Name = name;
        Email = email;
    }

    public DisplayName Name { get; private set; }

    public EmailAddress Email { get; private set; }

    public DateTimeOffset? EmailVerifiedAt { get; private set; }

    public string? ExternalSubject { get; private set; }

    public bool ForcePasswordReset { get; private set; }

    public bool MarketingEmailsEnabled { get; private set; }

    public static User Create(DisplayName name, EmailAddress email, DateTimeOffset now)
    {
        return new User(UserId.New(), name, email, now);
    }

    public void VerifyEmail(DateTimeOffset now)
    {
        EmailVerifiedAt = now;
        Touch(now);
    }

    public void LinkExternalSubject(string externalSubject, DateTimeOffset now)
    {
        ExternalSubject = DomainValidation.Required(externalSubject, nameof(ExternalSubject), 255);
        Touch(now);
    }
}
