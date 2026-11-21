using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Vessel.Domain.Services;

namespace Vessel.Application.ManagedServices.Templates;

/// <summary>
///     A <c>${VAR}</c>, <c>${VAR:-default}</c>, <c>${VAR:?}</c> or <c>$VAR</c> reference in a Compose document.
///     <c>Default</c> is the value after <c>:-</c> / <c>-</c>; <c>Required</c> is true for <c>:?</c> / <c>?</c> (Compose
///     fails when the variable is unset or empty).
/// </summary>
public sealed record ServiceTemplateVariableReference(string Name, string? Default, bool Required);

public static partial class ServiceTemplateVariables
{
    /// <summary>
    ///     Variable references in Compose interpolation syntax. <c>$$</c> is an escaped dollar and is skipped, as Compose
    ///     does.
    /// </summary>
    public static IReadOnlyList<ServiceTemplateVariableReference> References(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var references = new List<ServiceTemplateVariableReference>();
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '$') continue;
            if (index + 1 < text.Length && text[index + 1] == '$')
            {
                index++;
                continue;
            }

            Match match = ReferencePattern().Match(text, index);
            if (!match.Success || match.Index != index) continue;
            var name = match.Groups["braced"].Success ? match.Groups["braced"].Value : match.Groups["bare"].Value;
            var operation = match.Groups["op"].Value;
            string? defaultValue = operation.EndsWith('-') ? match.Groups["arg"].Value : null;
            references.Add(new ServiceTemplateVariableReference(name, defaultValue, operation.EndsWith('?')));
            index += match.Length - 1;
        }

        return references;
    }

    [GeneratedRegex(@"\$(?:\{(?<braced>[A-Za-z_][A-Za-z0-9_]*)(?:(?<op>:?[-?+])(?<arg>[^}]*))?\}|(?<bare>[A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();
}

/// <summary>Image pinning policy for imported templates (TPL-01).</summary>
public static partial class ServiceTemplateImagePolicy
{
    private static readonly HashSet<string> FloatingTags = new(StringComparer.OrdinalIgnoreCase)
        { "latest", "stable", "main", "master", "nightly", "edge", "dev", "develop", "beta", "rc" };

    /// <summary>
    ///     Returns null for a version-pinned image, a warning for an image that is pinned only to a major version
    ///     (<c>ghost:6</c>, <c>postgres:16-alpine</c>) or to a floating tag together with a digest, and throws for an
    ///     untagged image, a <c>:latest</c>/floating tag or an interpolated image reference.
    /// </summary>
    /// <exception cref="ServiceTemplateImportException">The image is not pinned.</exception>
    public static string? Check(string serviceName, string image)
    {
        if (string.IsNullOrWhiteSpace(image) || image.Contains('$', StringComparison.Ordinal) ||
            image.Any(char.IsWhiteSpace))
            throw new ServiceTemplateImportException(
                $"Service '{serviceName}' must use a literal image reference.");

        var digestIndex = image.IndexOf('@', StringComparison.Ordinal);
        var hasDigest = digestIndex >= 0;
        var name = hasDigest ? image[..digestIndex] : image;
        var lastSegment = name[(name.LastIndexOf('/') + 1)..];
        var colon = lastSegment.LastIndexOf(':');
        var tag = colon >= 0 ? lastSegment[(colon + 1)..] : null;

        if (hasDigest)
            return tag is null || FloatingTags.Contains(tag)
                ? $"Service '{serviceName}' pins image {image} by digest only; its tag does not show the version."
                : null;
        if (tag is null)
            throw new ServiceTemplateImportException(
                $"Service '{serviceName}' uses untagged image '{image}' (implicit :latest); pin a version.");
        if (FloatingTags.Contains(tag))
            throw new ServiceTemplateImportException(
                $"Service '{serviceName}' uses floating image tag '{image}'; pin a version.");
        return MajorOnlyTag().IsMatch(tag)
            ? $"Service '{serviceName}' image {image} is pinned to a major version only; minor updates can change it."
            : null;
    }

    [GeneratedRegex(@"^v?\d+(-[A-Za-z0-9.]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex MajorOnlyTag();
}

/// <summary>Generates magic-variable values with a cryptographic RNG (upstream <c>generateEnvValue</c>).</summary>
public static class ServiceMagicValueGenerator
{
    private const string Alphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const string LowercaseAlphanumeric = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    ///     Symbols safe in a double-quoted <c>.env</c> value and in Compose interpolation (no <c>$</c>, quotes, backslash,
    ///     <c>#</c> or whitespace). Upstream <c>Str::password(symbols: true)</c> uses a wider set.
    /// </summary>
    public const string Symbols = "!%^&*()-_=+[]{}:,.?~";

    public static string Generate(ServiceMagicVariable variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ServiceMagicGenerator generator = variable.Generator ??
                                          throw new ArgumentException("Only generated magic variables have values.",
                                              nameof(variable));
        return generator.Alphabet switch
        {
            ServiceMagicAlphabet.Alphanumeric => RandomNumberGenerator.GetString(Alphanumeric, generator.Length),
            ServiceMagicAlphabet.LowercaseAlphanumeric =>
                RandomNumberGenerator.GetString(LowercaseAlphanumeric, generator.Length),
            ServiceMagicAlphabet.AlphanumericWithSymbols =>
                RandomNumberGenerator.GetString(Alphanumeric + Symbols, generator.Length),
            ServiceMagicAlphabet.Hex => RandomNumberGenerator.GetHexString(generator.Length, true),
            ServiceMagicAlphabet.Base64OfRandomBytes =>
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(generator.Length)),
            _ => throw new ArgumentOutOfRangeException(nameof(variable))
        };
    }
}
