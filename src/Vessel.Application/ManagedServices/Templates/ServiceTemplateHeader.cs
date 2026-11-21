using System.Globalization;

namespace Vessel.Application.ManagedServices.Templates;

/// <summary>
///     Metadata from the leading comment block of a Coolify compose template (<c>templates/compose/*.yaml</c>):
///     <c># documentation:</c>, <c># slogan:</c>, <c># category:</c>, <c># tags:</c>, <c># logo:</c>, <c># port:</c>,
///     <c># minversion:</c>, <c># env_file:</c>, <c># ignore:</c>, <c># amd_only:</c>, <c># arm_only:</c> and
///     <c># outdated:</c>. Unknown keys are kept in <see cref="Other" />.
/// </summary>
public sealed record ServiceTemplateHeader(
    string? Documentation,
    string? Slogan,
    string? Category,
    IReadOnlyList<string> Tags,
    string? Logo,
    int? Port,
    string? MinimumVersion,
    string? EnvironmentFile,
    bool Ignore,
    bool AmdOnly,
    bool ArmOnly,
    bool Outdated,
    IReadOnlyDictionary<string, string> Other);

public static class ServiceTemplateHeaderParser
{
    /// <summary>
    ///     Reads <c># key: value</c> lines until the first line that is neither a comment nor blank. Repeated keys keep the
    ///     first value; a <c># port:</c> that is not a TCP port is a parse error.
    /// </summary>
    /// <exception cref="ServiceTemplateImportException">The header has an invalid port.</exception>
    public static ServiceTemplateHeader Parse(string templateText)
    {
        ArgumentNullException.ThrowIfNull(templateText);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(templateText.TrimStart('﻿'));
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (!trimmed.StartsWith('#')) break;
            var body = trimmed.TrimStart('#').Trim();
            var separator = body.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0) continue;
            var key = body[..separator].Trim();
            if (key.Length == 0 || key.Any(char.IsWhiteSpace)) continue;
            values.TryAdd(key, body[(separator + 1)..].Trim());
        }

        int? port = null;
        if (values.TryGetValue("port", out var portText) && portText.Length > 0)
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                parsed is < 1 or > 65535)
                throw new ServiceTemplateImportException($"Template header port '{portText}' is not a valid port.");
            port = parsed;
        }

        var tags = values.TryGetValue("tags", out var tagText)
            ? tagText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(tag => tag.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [];
        string[] known =
        [
            "documentation", "slogan", "category", "tags", "logo", "port", "minversion", "env_file", "ignore",
            "amd_only", "arm_only", "outdated"
        ];
        return new ServiceTemplateHeader(
            Text(values, "documentation"),
            Text(values, "slogan"),
            Text(values, "category"),
            tags,
            Text(values, "logo"),
            port,
            Text(values, "minversion"),
            Text(values, "env_file"),
            Flag(values, "ignore"),
            Flag(values, "amd_only"),
            Flag(values, "arm_only"),
            Flag(values, "outdated"),
            values.Where(pair => !known.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key.ToLowerInvariant(), pair => pair.Value, StringComparer.Ordinal));
    }

    private static string? Text(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
    }

    private static bool Flag(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) &&
               (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1");
    }
}
