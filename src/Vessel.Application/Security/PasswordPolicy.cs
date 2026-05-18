namespace Vessel.Application.Security;

public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    public static void Validate(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinimumLength)
            throw new InvalidOperationException($"Password must be at least {MinimumLength} characters.");

        if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
            throw new InvalidOperationException("Password must include uppercase, lowercase, and numeric characters.");
    }
}
