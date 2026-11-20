using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vessel.Application.Email;
using Vessel.Application.OutboundRequests;
using Vessel.Domain.Notifications;

namespace Vessel.Infrastructure.Email;

/// <summary>
///     Resend HTTP API transport (<c>POST https://api.resend.com/emails</c>). The endpoint is fixed, so no user URL is
///     involved. An email that Resend does not accept is reported as failed (upstream v4.4.0); the response body is
///     never stored or logged because it can echo addresses.
/// </summary>
public sealed class ResendEmailTransport(HttpClient httpClient) : IResendEmailTransport
{
    public const string HttpClientName = "Vessel.Email.Resend";

    public async Task<EmailSendResult> SendAsync(string apiKey, EmailSender from, EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return EmailSendResult.Failed("The Resend API key is not configured.", NotificationFailureKind.Configuration);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(message);

        using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = JsonContent.Create(new ResendEmailRequest(
                FormatSender(from),
                message.To.ToArray(),
                message.Subject.ReplaceLineEndings(" "),
                message.TextBody,
                string.IsNullOrEmpty(message.HtmlBody) ? null : message.HtmlBody))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Options.Set(OutboundRequestRedaction.RedactUrlOption, true);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var id = await ReadIdAsync(response, cancellationToken);
                return id is null
                    ? EmailSendResult.Failed("Resend did not accept the email.", NotificationFailureKind.Transient)
                    : EmailSendResult.Sent(id);
            }

            var status = (int)response.StatusCode;
            var kind = status switch
            {
                429 or >= 500 => NotificationFailureKind.Transient,
                401 or 403 => NotificationFailureKind.Configuration,
                _ => NotificationFailureKind.Payload
            };
            return EmailSendResult.Failed($"Resend rejected the email with HTTP {status}.", kind);
        }
        catch (HttpRequestException exception)
        {
            return EmailSendResult.Failed($"Resend delivery failed: {exception.GetType().Name}.",
                NotificationFailureKind.Transient);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return EmailSendResult.Failed("Resend delivery timed out.", NotificationFailureKind.Transient);
        }
    }

    internal static string FormatSender(EmailSender from)
    {
        if (string.IsNullOrWhiteSpace(from.Name))
            return from.Address;

        var name = from.Name.Replace("\"", "'", StringComparison.Ordinal).ReplaceLineEndings(" ");
        return $"\"{name}\" <{from.Address}>";
    }

    private static async Task<string?> ReadIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("id", out var id)
                   && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ResendEmailRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("from")] string From,
        [property: System.Text.Json.Serialization.JsonPropertyName("to")] string[] To,
        [property: System.Text.Json.Serialization.JsonPropertyName("subject")] string Subject,
        [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text,
        [property: System.Text.Json.Serialization.JsonPropertyName("html")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Html);
}
