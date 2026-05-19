using Vessel.Domain.Common;

namespace Vessel.Domain.Secrets;

public sealed class SecretValue : Entity<SecretValueId>
{
    private SecretValue()
    {
    }

    private SecretValue(
        SecretValueId id,
        SecretReferenceId secretReferenceId,
        string cipherText,
        string nonce,
        string tag,
        string keyVersion,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        SecretReferenceId = secretReferenceId;
        CipherText = DomainValidation.Required(cipherText, nameof(CipherText), 12000);
        Nonce = DomainValidation.Required(nonce, nameof(Nonce), 128);
        Tag = DomainValidation.Required(tag, nameof(Tag), 128);
        KeyVersion = DomainValidation.Required(keyVersion, nameof(KeyVersion), 80);
    }

    public SecretReferenceId SecretReferenceId { get; private set; }

    public string CipherText { get; private set; } = string.Empty;

    public string Nonce { get; private set; } = string.Empty;

    public string Tag { get; private set; } = string.Empty;

    public string KeyVersion { get; private set; } = string.Empty;

    public static SecretValue Create(
        SecretReferenceId secretReferenceId,
        string cipherText,
        string nonce,
        string tag,
        string keyVersion,
        DateTimeOffset now)
    {
        return new SecretValue(SecretValueId.New(), secretReferenceId, cipherText, nonce, tag, keyVersion, now);
    }

    public void Replace(string cipherText, string nonce, string tag, string keyVersion, DateTimeOffset now)
    {
        CipherText = DomainValidation.Required(cipherText, nameof(CipherText), 12000);
        Nonce = DomainValidation.Required(nonce, nameof(Nonce), 128);
        Tag = DomainValidation.Required(tag, nameof(Tag), 128);
        KeyVersion = DomainValidation.Required(keyVersion, nameof(KeyVersion), 80);
        Touch(now);
    }
}
