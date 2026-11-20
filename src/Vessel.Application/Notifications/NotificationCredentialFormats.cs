using System.Text.RegularExpressions;

namespace Vessel.Application.Notifications;

public static partial class NotificationCredentialFormats
{
    /// <summary>
    ///     Telegram bot tokens are placed in the request path (<c>/bot{token}/sendMessage</c>), so the format is
    ///     strict: anything else (slashes, dots, percent-encoding) could redirect the request to another API path.
    /// </summary>
    public static bool IsValidTelegramBotToken(string? token)
    {
        return !string.IsNullOrWhiteSpace(token) && TelegramBotTokenPattern().IsMatch(token);
    }

    [GeneratedRegex("^[0-9]{1,20}:[A-Za-z0-9_-]{20,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex TelegramBotTokenPattern();
}
