using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

public readonly record struct RepositoryUrl
{
    public const int MaxLength = 2048;

    public RepositoryUrl(string value)
    {
        var normalized = DomainValidation.Required(value, nameof(RepositoryUrl), MaxLength);
        var isHttps = Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri)
                      && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
        var isSsh = normalized.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
                    && normalized.Contains(':', StringComparison.Ordinal);

        if (!isHttps && !isSsh) throw new DomainException("Repository URL must be an HTTP(S) or SSH Git URL.");

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
