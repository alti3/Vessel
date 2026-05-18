using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Domain;

public sealed class UserSecurityTests
{
    [Fact]
    public void FailedLoginLocksUserAtConfiguredThreshold()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        User user = User.Create(new DisplayName("Test User"), new EmailAddress("test@example.com"), now);

        user.RecordFailedLogin(lockoutThreshold: 2, TimeSpan.FromMinutes(15), now);
        Assert.False(user.IsLockedOut(now));

        user.RecordFailedLogin(lockoutThreshold: 2, TimeSpan.FromMinutes(15), now);

        Assert.True(user.IsLockedOut(now.AddMinutes(1)));
    }
}
