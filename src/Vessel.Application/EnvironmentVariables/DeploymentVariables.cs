using Vessel.Domain.Common;
using Vessel.Domain.EnvironmentVariables;

namespace Vessel.Application.EnvironmentVariables;

/// <summary>
///     One variable as a deployment sees it, after scope selection and secret resolution, before interpolation.
///     <see cref="Value" /> is null for a preserved Compose null (written as unset, never as an empty string).
/// </summary>
public sealed record DeploymentVariable(
    string Key,
    string? Value,
    bool IsSecret = false,
    bool IsLiteral = false,
    bool IsMultiline = false,
    bool IsBuildTime = false,
    bool IsRuntime = true,
    bool UsesLegacyEscaping = false,
    bool IsDefault = false)
{
    /// <summary>
    ///     Upstream semantics (<c>environmentVariableAllowsInterpolation</c>): only plain single-line values interpolate
    ///     <c>$NAME</c>/<c>${NAME}</c>. Literal, multiline, JSON and secret values are passed exactly.
    /// </summary>
    public bool AllowsInterpolation
    {
        get
        {
            if (IsLiteral || IsMultiline || IsSecret || IsDefault || Value is null) return false;
            var trimmed = Value.TrimStart();
            return !(trimmed.StartsWith('{') || trimmed.StartsWith('['));
        }
    }

    public static DeploymentVariable From(EnvironmentVariable variable, string? value)
    {
        ArgumentNullException.ThrowIfNull(variable);
        return new DeploymentVariable(variable.Key.Value, value,
            variable.ValueKind == EnvironmentVariableValueKind.Secret, variable.IsLiteral, variable.IsMultiline,
            variable.IsBuildTime, variable.IsRuntime, variable.UsesLegacyEscaping);
    }
}

/// <summary>A resolved variable: its final value plus how it must be written.</summary>
public sealed record ResolvedVariable(string Key, string? Value, bool IsSecret, bool UsesLegacyEscaping,
    string? RawValue);

/// <summary>Resolved runtime and build values of one deployment, in deterministic order.</summary>
public sealed record ResolvedDeploymentEnvironment(
    IReadOnlyList<ResolvedVariable> Runtime,
    IReadOnlyList<ResolvedVariable> Build,
    IReadOnlyList<string> UnresolvedReferences)
{
    /// <summary>All values (runtime and build) for log redaction.</summary>
    public IReadOnlyDictionary<string, string> AllValues
    {
        get
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ResolvedVariable variable in Runtime.Concat(Build))
                if (variable.Value is not null)
                    values[variable.Key] = variable.Value;
            return values;
        }
    }

    public IReadOnlySet<string> SecretKeys =>
        Runtime.Concat(Build).Where(variable => variable.IsSecret).Select(variable => variable.Key)
            .ToHashSet(StringComparer.Ordinal);
}

/// <summary>
///     Resolves deployment variables (DEP-10): later definitions override earlier ones (scope order), values are
///     interpolated by Vessel itself against the deployment's own variables only — never the control plane's process
///     environment — and every value is then written exactly. Runtime-only values never reach the build and build-only
///     values never reach the container.
/// </summary>
public static class DeploymentEnvironmentResolver
{
    public static ResolvedDeploymentEnvironment Resolve(IEnumerable<DeploymentVariable> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var unresolved = new List<string>();
        DeploymentVariable[] all = variables.ToArray();
        IReadOnlyList<ResolvedVariable> runtime = Resolve(all.Where(variable => variable.IsRuntime), unresolved);
        IReadOnlyList<ResolvedVariable> build = Resolve(all.Where(variable => variable.IsBuildTime), unresolved);
        return new ResolvedDeploymentEnvironment(runtime, build,
            unresolved.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<ResolvedVariable> Resolve(IEnumerable<DeploymentVariable> variables,
        List<string> unresolved)
    {
        var order = new List<string>();
        var values = new Dictionary<string, ResolvedVariable>(StringComparer.Ordinal);
        foreach (DeploymentVariable variable in variables)
        {
            string? value = variable.Value;
            if (value is not null && variable.AllowsInterpolation && !variable.UsesLegacyEscaping)
                value = EnvironmentValueInterpolator.Interpolate(value,
                    name => values.TryGetValue(name, out ResolvedVariable? current) ? current.Value : null,
                    unresolved);
            if (!values.ContainsKey(variable.Key)) order.Add(variable.Key);
            values[variable.Key] = new ResolvedVariable(variable.Key, value, variable.IsSecret,
                variable.UsesLegacyEscaping && !variable.IsDefault, variable.Value);
        }

        return order.Select(key => values[key]).ToArray();
    }
}

/// <summary>
///     Compose-style interpolation of one value, performed by Vessel: <c>$$</c> is a literal <c>$</c>; <c>$NAME</c>,
///     <c>${NAME}</c>, <c>${NAME:-default}</c>, <c>${NAME-default}</c>, <c>${NAME:+alt}</c>, <c>${NAME+alt}</c>,
///     <c>${NAME:?message}</c> and <c>${NAME?message}</c> follow the Compose rules. Any other <c>$</c> stays literal.
///     Names that are not defined resolve to an empty string and are reported (by name only, never by value).
/// </summary>
public static class EnvironmentValueInterpolator
{
    private const int MaxDepth = 8;

    public static string Interpolate(string value, Func<string, string?> lookup, ICollection<string>? unresolved = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(lookup);
        return Interpolate(value, lookup, unresolved, 0);
    }

    private static string Interpolate(string value, Func<string, string?> lookup, ICollection<string>? unresolved,
        int depth)
    {
        if (depth > MaxDepth) throw new DomainException("Environment variable references are nested too deeply.");
        if (!value.Contains('$', StringComparison.Ordinal)) return value;
        var builder = new System.Text.StringBuilder(value.Length);
        var index = 0;
        while (index < value.Length)
        {
            var character = value[index];
            if (character != '$' || index + 1 >= value.Length)
            {
                builder.Append(character);
                index++;
                continue;
            }

            var next = value[index + 1];
            if (next == '$')
            {
                builder.Append('$');
                index += 2;
                continue;
            }

            if (IsNameStart(next))
            {
                var end = index + 1;
                while (end < value.Length && IsNamePart(value[end])) end++;
                var name = value[(index + 1)..end];
                builder.Append(Lookup(name, lookup, unresolved) ?? string.Empty);
                index = end;
                continue;
            }

            if (next == '{')
            {
                var close = FindClosingBrace(value, index + 2);
                if (close < 0)
                {
                    builder.Append(character);
                    index++;
                    continue;
                }

                builder.Append(ExpandBraced(value[(index + 2)..close], lookup, unresolved, depth));
                index = close + 1;
                continue;
            }

            builder.Append(character);
            index++;
        }

        return builder.ToString();
    }

    private static string ExpandBraced(string expression, Func<string, string?> lookup,
        ICollection<string>? unresolved, int depth)
    {
        var nameEnd = 0;
        while (nameEnd < expression.Length && (IsNamePart(expression[nameEnd]) || expression[nameEnd] == '.'))
            nameEnd++;
        var name = expression[..nameEnd];
        if (name.Length == 0 || !IsNameStart(name[0])) return "${" + expression + "}";
        var rest = expression[nameEnd..];
        var current = lookup(name);
        if (rest.Length == 0)
        {
            if (current is null) unresolved?.Add(name);
            return current ?? string.Empty;
        }

        var colon = rest.StartsWith(':');
        var operation = colon ? rest.Length > 1 ? rest[1] : '\0' : rest[0];
        var argument = rest[(colon ? 2 : 1)..];
        var isSet = colon ? !string.IsNullOrEmpty(current) : current is not null;
        switch (operation)
        {
            case '-':
                return isSet ? current! : Interpolate(argument, lookup, unresolved, depth + 1);
            case '+':
                return isSet ? Interpolate(argument, lookup, unresolved, depth + 1) : string.Empty;
            case '?':
                if (isSet) return current!;
                throw new DomainException(
                    $"Required environment variable {name} is not set" +
                    (argument.Length == 0 ? "." : ": " + Interpolate(argument, lookup, null, depth + 1)));
            default:
                return "${" + expression + "}";
        }
    }

    private static string? Lookup(string name, Func<string, string?> lookup, ICollection<string>? unresolved)
    {
        var value = lookup(name);
        if (value is null) unresolved?.Add(name);
        return value;
    }

    private static int FindClosingBrace(string value, int start)
    {
        var nesting = 0;
        for (var index = start; index < value.Length; index++)
        {
            if (value[index] == '{' && index > 0 && value[index - 1] == '$') nesting++;
            else if (value[index] == '}')
            {
                if (nesting == 0) return index;
                nesting--;
            }
        }

        return -1;
    }

    private static bool IsNameStart(char character)
    {
        return character is '_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    }

    private static bool IsNamePart(char character)
    {
        return IsNameStart(character) || character is >= '0' and <= '9';
    }
}
