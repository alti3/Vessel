using Vessel.Application.Deployments;
using Vessel.Application.Security;
using Vessel.Infrastructure.Security;

namespace Vessel.IntegrationTests.Security;

/// <summary>
///     SEC-09 / DEP-03: golden cases ported from Coolify's <c>tests/Unit/SensitiveDataRedactorTest.php</c>, plus the
///     deployment log context (every secret value, plain values on opt-in, multiline and streamed PEM keys).
/// </summary>
public sealed class SecretRedactorParityTests
{
    private const string R = SecretRedactor.Replacement;
    private readonly SecretRedactor _redactor = new();

    [Fact]
    public void Known_values_and_their_encoded_variants_are_replaced_longest_first()
    {
        string[] known = ["coolify-secret", "coolify-secret-longer", "secret with spaces", "quote\"secret"];

        var result = _redactor.Redact("coolify-secret-longer coolify-secret secret%20with%20spaces quote\\\"secret",
            new RedactionContext(KnownValues: known));

        Assert.Equal($"{R} {R} {R} {R}", result);
    }

    [Fact]
    public void Short_known_values_are_not_replaced_in_unstructured_text()
    {
        Assert.Equal("mode=dev enabled=yes count=1",
            _redactor.Redact("mode=dev enabled=yes count=1", new RedactionContext(KnownValues: ["dev", "yes", "1"])));
    }

    [Fact]
    public void Authorization_headers_are_redacted_completely()
    {
        Assert.Equal($"Authorization: {R}\nstatus=ok",
            _redactor.Redact("Authorization: Bearer coolify-canary-secret-scheduled\nstatus=ok"));
    }

    [Fact]
    public void Url_credentials_bearer_tokens_and_private_keys_are_redacted_and_ansi_is_stripped_in_logs()
    {
        var key = "-----BEGIN PRIVATE KEY-----\nsecret-key-data\n-----END PRIVATE KEY-----";

        var result = _redactor.Redact($"\u001b[31mhttps://user:password@example.com\u001b[0m Bearer aaa.bbb.ccc {key}",
            new RedactionContext(IsLogOutput: true));

        Assert.DoesNotContain("\u001b[31m", result, StringComparison.Ordinal);
        Assert.DoesNotContain("password", result, StringComparison.Ordinal);
        Assert.DoesNotContain("aaa.bbb.ccc", result, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key-data", result, StringComparison.Ordinal);
        Assert.Contains($"https://user:{R}@example.com", result, StringComparison.Ordinal);
        Assert.Contains($"Bearer {R}", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Url_credential_redaction_never_spans_lines()
    {
        var result = _redactor.Redact("postgres://user:\nnext line with @ sign");

        Assert.Equal("postgres://user:\nnext line with @ sign", result);
    }

    [Fact]
    public void Short_locked_values_are_always_redacted_but_only_as_whole_tokens()
    {
        Assert.Equal($"pin {R} mode dev",
            _redactor.Redact("pin 4821 mode dev", new RedactionContext(["4821"], KnownValues: ["dev"])));
        Assert.Equal("database ab_c cab 9ab",
            _redactor.Redact("database ab_c cab 9ab", new RedactionContext(["ab"])));
        Assert.Equal($"password {R} user={R} \"{R}\" postgres://u:{R}@host",
            _redactor.Redact("password xy user=xy \"xy\" postgres://u:xy@host", new RedactionContext(["xy"])));
    }

    [Fact]
    public void Quoted_assignment_values_with_spaces_are_hidden_completely()
    {
        Assert.Equal($"password=\"{R}\" PASSWORD='{R}' next=1",
            _redactor.Redact("password=\"first second\" PASSWORD='third fourth' next=1"));
    }

    [Fact]
    public void Keys_ending_with_a_sensitive_name_are_redacted_but_counts_are_not()
    {
        Assert.Equal($"smtp_password={R} \"db-password\": \"{R}\" max_tokens=100",
            _redactor.Redact("smtp_password=hunter2 \"db-password\": \"p w\" max_tokens=100"));
    }

    [Fact]
    public void Json_stays_valid_after_redaction()
    {
        Assert.Equal($"{{\"token\": \"{R}\", \"password\": \"{R}\", \"name\": \"demo\"}}",
            _redactor.Redact("{\"token\": \"abc\", \"password\": \"a\\\"b\", \"name\": \"demo\"}"));
        Assert.Equal($"{{'secret': '{R}'}}", _redactor.Redact("{'secret': 'abc'}"));
    }

    [Fact]
    public void Prose_and_working_directories_are_not_treated_as_assignments()
    {
        Assert.Equal("Failed to refresh token: Connection refused",
            _redactor.Redact("Failed to refresh token: Connection refused"));
        Assert.Equal("PWD=/artifacts/abc", _redactor.Redact("PWD=/artifacts/abc"));
    }

    [Theory]
    [InlineData("password=abc", "password=" + R)]
    [InlineData("\"password\": \"abc\"", "\"password\": \"" + R + "\"")]
    [InlineData("\"token\": abc123,", "\"token\": " + R + ",")]
    [InlineData("API_TOKEN=abc", "API_TOKEN=" + R)]
    [InlineData("POSTGRES_PASSWORD: abc next", "POSTGRES_PASSWORD: " + R + " next")]
    [InlineData("smtp_password: abc next", "smtp_password: " + R + " next")]
    [InlineData("password: correcthorse\nnext", "password: " + R + "\nnext")]
    [InlineData("Password: hunter2 was set", "Password: " + R + " was set")]
    [InlineData("Authorization: Bearer x", "Authorization: " + R)]
    [InlineData("docker build --build-arg 'COOLIFY_BUILD_SECRETS_HASH=abc123def' --build-arg 'COOLIFY_URL'",
        "docker build --build-arg 'COOLIFY_BUILD_SECRETS_HASH=" + R + "' --build-arg 'COOLIFY_URL'")]
    [InlineData("export \"DB_PASSWORD=first second\" && next", "export \"DB_PASSWORD=" + R + "\" && next")]
    [InlineData("--env 'api_token=abc def' --rm", "--env 'api_token=" + R + "' --rm")]
    public void Real_secret_assignments_are_redacted(string text, string expected)
    {
        Assert.Equal(expected, _redactor.Redact(text));
    }

    [Theory]
    [InlineData("clone with ghp_abcdefghijklmnopqrstuvwxyz0123456789 done")]
    [InlineData("clone with ghs_abc.def-ghi_jklmnopqrstuvwxyz0123456789ABC done")]
    [InlineData("token github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz done")]
    [InlineData("gitlab glpat-abcdefghijklmnopqrstu done")]
    [InlineData("aws AKIAABCDEFGHIJKLMNOP done")]
    [InlineData("https://x-access-token:ghs_installation_token@github.com/org/repo.git")]
    [InlineData("https://oauth2:glpat-token@gitlab.com/org/repo.git")]
    public void Provider_tokens_are_redacted(string text)
    {
        var result = _redactor.Redact(text);

        Assert.Contains(R, result, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstu", result, StringComparison.Ordinal);
        Assert.DoesNotContain("installation_token", result, StringComparison.Ordinal);
        Assert.DoesNotContain("glpat-token", result, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIAABCDEFGHIJKLMNOP", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Unterminated_private_key_is_hidden_to_the_end()
    {
        var result = _redactor.Redact("before password=abc\n-----BEGIN PRIVATE KEY-----\nsecret-key-line\ntail");

        Assert.StartsWith($"before password={R}\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key-line", result, StringComparison.Ordinal);
        Assert.DoesNotContain("tail", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Emails_are_hidden_only_in_log_output()
    {
        Assert.Equal("root@server.example.com:~$", _redactor.Redact("root@server.example.com:~$"));
        Assert.Equal($"author {R}", _redactor.Redact("author dev@example.com", new RedactionContext(IsLogOutput: true)));
    }

    [Fact]
    public void Deployment_log_context_hides_every_secret_value_and_plain_values_only_on_opt_in()
    {
        var environment = new Dictionary<string, string>
        {
            ["DATABASE_PASSWORD"] = "pw",
            ["PUBLIC_SITE_NAME"] = "my-public-site",
            ["VESSEL_APPLICATION_ID"] = "4f1c2d6e-0000-0000-0000-000000000001"
        };
        var secrets = new HashSet<string> { "DATABASE_PASSWORD" };

        var secretsOnly = new DeploymentLogRedactor(_redactor,
            DeploymentLogRedactor.CreateContext(environment, secrets, redactAllValues: false));
        var everything = new DeploymentLogRedactor(_redactor,
            DeploymentLogRedactor.CreateContext(environment, secrets, redactAllValues: true));
        var line = "Step 3: echo pw my-public-site 4f1c2d6e-0000-0000-0000-000000000001";

        Assert.Equal($"Step 3: echo {R} my-public-site 4f1c2d6e-0000-0000-0000-000000000001",
            secretsOnly.Redact(line));
        Assert.Equal($"Step 3: echo {R} {R} 4f1c2d6e-0000-0000-0000-000000000001", everything.Redact(line));
    }

    [Fact]
    public void Multiline_secret_values_are_redacted_line_by_line_in_streamed_output()
    {
        var certificate = "MIIBszCCAVmgAwIBAgIUQ1\nZXhhbXBsZS1jZXJ0aWZpY2F0ZQ==";
        var environment = new Dictionary<string, string> { ["TLS_CERT"] = certificate };
        var log = new DeploymentLogRedactor(_redactor,
            DeploymentLogRedactor.CreateContext(environment, new HashSet<string> { "TLS_CERT" }, false));

        Assert.Equal($"#5 {R}", log.Redact("#5 MIIBszCCAVmgAwIBAgIUQ1"));
        Assert.Equal($"#5 {R}", log.Redact("#5 ZXhhbXBsZS1jZXJ0aWZpY2F0ZQ=="));
    }

    [Fact]
    public void Private_key_streamed_over_several_lines_is_hidden_until_its_end_marker()
    {
        var log = new DeploymentLogRedactor(_redactor);

        Assert.Equal($"key: {R}", log.Redact("key: -----BEGIN OPENSSH PRIVATE KEY-----"));
        Assert.Equal(R, log.Redact("b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQ"));
        Assert.Equal(R, log.Redact("-----END OPENSSH PRIVATE KEY-----"));
        Assert.Equal("Step 4 done", log.Redact("Step 4 done"));
    }

    [Fact]
    public void Build_output_that_echoes_values_is_redacted_with_the_deployment_context()
    {
        var environment = new Dictionary<string, string> { ["API_SECRET"] = "s3cr3t-value-123" };
        var log = new DeploymentLogRedactor(_redactor,
            DeploymentLogRedactor.CreateContext(environment, new HashSet<string> { "API_SECRET" }, false));

        Assert.Equal($"#7 [build 2/4] RUN echo {R} > /tmp/x",
            log.Redact("#7 [build 2/4] RUN echo s3cr3t-value-123 > /tmp/x"));
        Assert.Equal($"value {R}", log.Redact("value czNjcjN0LXZhbHVlLTEyMw=="));
    }
}
