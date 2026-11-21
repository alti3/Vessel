using System.Globalization;
using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.Services;

/// <summary>What a Coolify-format magic variable resolves to.</summary>
public enum ServiceMagicVariableKind
{
    /// <summary><c>SERVICE_URL_&lt;NAME&gt;[_&lt;PORT&gt;]</c>: the public URL (scheme, host and path) of a routed service.</summary>
    Url,

    /// <summary><c>SERVICE_FQDN_&lt;NAME&gt;[_&lt;PORT&gt;]</c>: the public host name (and path) without scheme.</summary>
    Fqdn,

    /// <summary><c>SERVICE_&lt;COMMAND&gt;_&lt;NAME&gt;</c>: a value generated once per service instance.</summary>
    Generated
}

/// <summary>
///     How a generated magic variable value is produced (upstream <c>generateEnvValue</c>). <c>Length</c> is the number
///     of output characters, or of random bytes for <see cref="ServiceMagicAlphabet.Base64OfRandomBytes" />.
/// </summary>
public sealed record ServiceMagicGenerator(ServiceMagicAlphabet Alphabet, int Length);

public enum ServiceMagicAlphabet
{
    /// <summary>Letters and digits (<c>A-Za-z0-9</c>).</summary>
    Alphanumeric,

    /// <summary>Lower-case letters and digits (<c>a-z0-9</c>).</summary>
    LowercaseAlphanumeric,

    /// <summary>Letters, digits and a Compose/.env-safe symbol set (no quotes, backslash or <c>$</c>).</summary>
    AlphanumericWithSymbols,

    /// <summary>Lower-case hexadecimal characters.</summary>
    Hex,

    /// <summary>Standard Base64 of <see cref="ServiceMagicGenerator.Length" /> random bytes.</summary>
    Base64OfRandomBytes
}

/// <summary>
///     A Coolify service-template magic variable (TPL-01). Names follow upstream
///     <c>bootstrap/helpers/services.php::parseServiceEnvironmentVariable</c> and
///     <c>EnvironmentVariable::GENERATED_MAGIC_VARIABLE_COMMANDS</c>:
///     <list type="bullet">
///         <item><c>SERVICE_URL_APP</c>, <c>SERVICE_URL_APP_3000</c>, <c>SERVICE_FQDN_APP</c>, <c>SERVICE_FQDN_APP_3000</c></item>
///         <item><c>SERVICE_PASSWORD_DB</c>, <c>SERVICE_PASSWORD_64_ADMIN</c>, <c>SERVICE_USER_DB</c>, <c>SERVICE_BASE64_64_KEY</c>, ...</item>
///     </list>
///     The Supabase JWT commands (<c>SUPABASEANON</c>/<c>SUPABASESERVICE</c>) are not supported and do not parse.
/// </summary>
public sealed partial record ServiceMagicVariable
{
    public const string Prefix = "SERVICE_";
    public const int MaxNameLength = 128;

    /// <summary>Generator commands, longest first so <c>PASSWORD_64_X</c> is never read as <c>PASSWORD</c> + <c>64_X</c>.</summary>
    private static readonly (string Command, ServiceMagicGenerator Generator)[] Commands =
        new (string Command, ServiceMagicGenerator Generator)[]
            {
                ("PASSWORD", new(ServiceMagicAlphabet.Alphanumeric, 32)),
                ("PASSWORD_64", new(ServiceMagicAlphabet.Alphanumeric, 64)),
                ("PASSWORDWITHSYMBOLS", new(ServiceMagicAlphabet.AlphanumericWithSymbols, 32)),
                ("PASSWORDWITHSYMBOLS_64", new(ServiceMagicAlphabet.AlphanumericWithSymbols, 64)),
                // Upstream "BASE64" is not Base64: it is a random alphanumeric string.
                ("BASE64", new(ServiceMagicAlphabet.Alphanumeric, 32)),
                ("BASE64_32", new(ServiceMagicAlphabet.Alphanumeric, 32)),
                ("BASE64_64", new(ServiceMagicAlphabet.Alphanumeric, 64)),
                ("BASE64_128", new(ServiceMagicAlphabet.Alphanumeric, 128)),
                ("REALBASE64", new(ServiceMagicAlphabet.Base64OfRandomBytes, 32)),
                ("REALBASE64_32", new(ServiceMagicAlphabet.Base64OfRandomBytes, 32)),
                ("REALBASE64_64", new(ServiceMagicAlphabet.Base64OfRandomBytes, 64)),
                ("REALBASE64_128", new(ServiceMagicAlphabet.Base64OfRandomBytes, 128)),
                ("HEX_32", new(ServiceMagicAlphabet.Hex, 32)),
                ("HEX_64", new(ServiceMagicAlphabet.Hex, 64)),
                ("HEX_128", new(ServiceMagicAlphabet.Hex, 128)),
                ("USER", new(ServiceMagicAlphabet.Alphanumeric, 16)),
                ("LOWERCASEUSER", new(ServiceMagicAlphabet.LowercaseAlphanumeric, 16))
            }
            .OrderByDescending(command => command.Command.Length)
            .ToArray();

    private ServiceMagicVariable(string name, ServiceMagicVariableKind kind, string identifier, string? command,
        int? port, ServiceMagicGenerator? generator)
    {
        Name = name;
        Kind = kind;
        Identifier = identifier;
        Command = command;
        Port = port;
        Generator = generator;
    }

    /// <summary>The full variable name, for example <c>SERVICE_URL_APP_3000</c>.</summary>
    public string Name { get; }

    public ServiceMagicVariableKind Kind { get; }

    /// <summary>The name part (<c>APP</c> in <c>SERVICE_URL_APP_3000</c>, <c>ADMIN</c> in <c>SERVICE_PASSWORD_64_ADMIN</c>).</summary>
    public string Identifier { get; }

    /// <summary>Generator command (<c>PASSWORD_64</c>) for <see cref="ServiceMagicVariableKind.Generated" /> variables.</summary>
    public string? Command { get; }

    /// <summary>Internal port of a URL/FQDN variable (<c>3000</c> in <c>SERVICE_URL_APP_3000</c>).</summary>
    public int? Port { get; }

    public ServiceMagicGenerator? Generator { get; }

    public bool IsRoute => Kind is ServiceMagicVariableKind.Url or ServiceMagicVariableKind.Fqdn;

    /// <summary>True when the name starts with <c>SERVICE_</c> and so is reserved for magic variables.</summary>
    public static bool IsReserved(string? name)
    {
        return name is not null && name.StartsWith(Prefix, StringComparison.Ordinal);
    }

    public static ServiceMagicVariable Parse(string? name)
    {
        return TryParse(name, out ServiceMagicVariable? variable, out var error)
            ? variable!
            : throw new DomainException($"Invalid magic variable '{name}': {error}");
    }

    public static bool TryParse(string? name, out ServiceMagicVariable? variable)
    {
        return TryParse(name, out variable, out _);
    }

    public static bool TryParse(string? name, out ServiceMagicVariable? variable, out string error)
    {
        variable = null;
        if (string.IsNullOrEmpty(name) || !IsReserved(name))
        {
            error = $"magic variables start with '{Prefix}'.";
            return false;
        }

        if (name.Length > MaxNameLength || !NamePattern().IsMatch(name))
        {
            error = "only upper-case letters, digits and '_' are allowed.";
            return false;
        }

        var rest = name[Prefix.Length..];
        if (rest.StartsWith("URL_", StringComparison.Ordinal))
            return TryRoute(rest, "URL_", ServiceMagicVariableKind.Url, name, out variable, out error);
        if (rest.StartsWith("FQDN_", StringComparison.Ordinal))
            return TryRoute(rest, "FQDN_", ServiceMagicVariableKind.Fqdn, name, out variable, out error);

        foreach ((var command, ServiceMagicGenerator generator) in Commands)
        {
            if (!rest.StartsWith(command + "_", StringComparison.Ordinal)) continue;
            var identifier = rest[(command.Length + 1)..];
            if (!IsIdentifier(identifier)) continue;
            variable = new ServiceMagicVariable(name, ServiceMagicVariableKind.Generated, identifier, command, null,
                generator);
            error = string.Empty;
            return true;
        }

        error = "unknown or unsupported magic variable command.";
        return false;
    }

    /// <summary>The URL and FQDN names, with and without the port, that share this route's identifier.</summary>
    public IReadOnlyList<string> RouteFamily()
    {
        if (!IsRoute) return [Name];
        var names = new List<string> { $"{Prefix}URL_{Identifier}", $"{Prefix}FQDN_{Identifier}" };
        if (Port.HasValue)
        {
            var port = Port.Value.ToString(CultureInfo.InvariantCulture);
            names.Add($"{Prefix}URL_{Identifier}_{port}");
            names.Add($"{Prefix}FQDN_{Identifier}_{port}");
        }

        return names;
    }

    public override string ToString()
    {
        return Name;
    }

    private static bool TryRoute(string rest, string marker, ServiceMagicVariableKind kind, string name,
        out ServiceMagicVariable? variable, out string error)
    {
        variable = null;
        error = string.Empty;
        var body = rest[marker.Length..];
        int? port = null;
        var separator = body.LastIndexOf('_');
        if (separator > 0 && body[(separator + 1)..].All(char.IsAsciiDigit) && separator < body.Length - 1)
        {
            var portText = body[(separator + 1)..];
            if (portText.Length > 5 || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var parsed) || parsed is < 1 or > 65535)
            {
                error = "the port must be between 1 and 65535.";
                return false;
            }

            port = parsed;
            body = body[..separator];
        }

        if (!IsIdentifier(body))
        {
            error = "the service name part is missing or invalid.";
            return false;
        }

        variable = new ServiceMagicVariable(name, kind, body, null, port, null);
        return true;
    }

    private static bool IsIdentifier(string value)
    {
        return value.Length > 0 && !value.StartsWith('_') && !value.EndsWith('_') && IdentifierPattern().IsMatch(value);
    }

    [GeneratedRegex("^SERVICE_[A-Z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
