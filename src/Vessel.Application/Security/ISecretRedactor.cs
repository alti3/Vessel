namespace Vessel.Application.Security;

public interface ISecretRedactor
{
    string Redact(string value, RedactionContext? context = null);

    byte[] RedactUtf8(byte[] value, RedactionContext? context = null);
}

public sealed record RedactionContext(
    IReadOnlyList<string>? SecretValues = null,
    IReadOnlyList<string>? PatternNames = null);
