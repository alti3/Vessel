using System.Text;
using System.Text.RegularExpressions;
using Vessel.Application.Processes;
using Vessel.Application.Security;

namespace Vessel.Application.Deployments;

/// <summary>
///     Redacts one deployment's log output with its full secret context (DEP-03/SEC-09): every secret value of the
///     deployment always, every other resolved environment value when the application enables
///     <c>RedactAllEnvValuesInLogs</c>, plus the redactor's patterns. Multiline secrets are matched line by line, and a
///     PEM private key streamed over several lines is hidden from its BEGIN line through its END line.
/// </summary>
public sealed partial class DeploymentLogRedactor
{
    private const string Replacement = "<REDACTED>";
    private readonly ISecretRedactor _redactor;
    private bool _insidePrivateKey;

    public DeploymentLogRedactor(ISecretRedactor redactor, RedactionContext? context = null)
    {
        _redactor = redactor;
        Context = context ?? new RedactionContext(IsLogOutput: true);
    }

    public RedactionContext Context { get; }

    /// <summary>The same context for process output (compose, build), so command output is redacted at the source too.</summary>
    public ProcessRedactionProfile ProcessProfile => new(Context.SecretValues ?? [], Context.PatternNames ?? [],
        Context.KnownValues, true);

    /// <summary>
    ///     Builds the context for a deployment's resolved environment. System values (<c>VESSEL_*</c> identifiers) are not
    ///     secrets and stay visible.
    /// </summary>
    public static RedactionContext CreateContext(IReadOnlyDictionary<string, string> environment,
        IReadOnlySet<string> secretKeys, bool redactAllValues)
    {
        var secrets = new List<string>();
        var known = new List<string>();
        foreach (var (key, value) in environment)
        {
            if (string.IsNullOrEmpty(value) || key.StartsWith("VESSEL_", StringComparison.Ordinal)) continue;
            if (secretKeys.Contains(key)) secrets.Add(value);
            else if (redactAllValues) known.Add(value);
        }

        return new RedactionContext(secrets, null, known, true);
    }

    /// <summary>Redacts one log message (one streamed line, or a multi-line system message).</summary>
    public string Redact(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        if (!message.Contains('\n', StringComparison.Ordinal) && !_insidePrivateKey &&
            !PrivateKeyBegin().IsMatch(message))
            return _redactor.Redact(message, Context);

        var builder = new StringBuilder(message.Length);
        var lines = message.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0) builder.Append('\n');
            builder.Append(RedactLine(lines[index]));
        }

        return builder.ToString();
    }

    private string RedactLine(string line)
    {
        if (_insidePrivateKey)
        {
            if (PrivateKeyEnd().IsMatch(line)) _insidePrivateKey = false;
            return Replacement;
        }

        Match begin = PrivateKeyBegin().Match(line);
        if (begin.Success && !PrivateKeyEnd().IsMatch(line))
        {
            _insidePrivateKey = true;
            return _redactor.Redact(line[..begin.Index], Context) + Replacement;
        }

        return _redactor.Redact(line, Context);
    }

    [GeneratedRegex("-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKeyBegin();

    [GeneratedRegex("-----END [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKeyEnd();
}
