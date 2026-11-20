namespace Vessel.Application.Security;

public interface ISecretRedactor
{
    string Redact(string value, RedactionContext? context = null);

    byte[] RedactUtf8(byte[] value, RedactionContext? context = null);
}

/// <summary>
///     Extra knowledge for one redaction (Coolify <c>SensitiveDataRedactor::redactText</c>).
/// </summary>
/// <param name="SecretValues">
///     Locked values that are always replaced whatever their length (secrets, credentials). Values shorter than eight
///     characters are replaced only as whole tokens so they never hit inside ordinary words.
/// </param>
/// <param name="PatternNames">Restricts the named patterns applied; null or empty applies all of them.</param>
/// <param name="KnownValues">
///     Plain values (for example every resolved environment value when <c>redact_all_env_values_in_logs</c> is on).
///     They are replaced only when at least eight characters long, so short values such as <c>dev</c> do not mangle logs.
/// </param>
/// <param name="IsLogOutput">
///     The text is persisted or streamed log output: ANSI escapes and control characters are stripped first and email
///     addresses are hidden. Interactive terminal output keeps these.
/// </param>
public sealed record RedactionContext(
    IReadOnlyList<string>? SecretValues = null,
    IReadOnlyList<string>? PatternNames = null,
    IReadOnlyList<string>? KnownValues = null,
    bool IsLogOutput = false);
