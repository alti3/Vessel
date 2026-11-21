using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel.Application.Git;
using Vessel.Application.Processes;
using Vessel.Domain.Sources;
using Vessel.Infrastructure.Git;
using Vessel.Infrastructure.Processes;
using Vessel.Infrastructure.Security;
using Vessel.Infrastructure.Sources;

namespace Vessel.IntegrationTests.SourceControl;

/// <summary>DEP-08: credentials reach git only through the askpass helper / identity file, never the command line.</summary>
public sealed class GitAuthenticationScopeTests
{
    private readonly DotNetProcessRunner _runner = new(new SecretRedactor(), TimeProvider.System);

    [Fact]
    public async Task Askpass_answers_only_for_the_bound_host()
    {
        if (OperatingSystem.IsWindows()) return;
        using GitAuthenticationScope scope = GitAuthenticationScope.Create(new GitAuthentication(
            new GitHttpsCredential("git.example.com", "deploy", "tok3n-value")));

        Assert.Equal("deploy", await AskAsync(scope, "Username for 'https://git.example.com': "));
        Assert.Equal("tok3n-value", await AskAsync(scope, "Password for 'https://deploy@git.example.com': "));
        Assert.Null(await AskAsync(scope, "Password for 'https://deploy@attacker.example.net': "));
        Assert.Null(await AskAsync(scope, "Password for 'http://deploy@git.example.com': "));
        Assert.Null(await AskAsync(scope, "Username for 'https://git.example.com.attacker.net': "));
        Assert.DoesNotContain(scope.Environment.Where(pair => pair.Key != "VESSEL_GIT_PASSWORD"),
            pair => pair.Value?.Contains("tok3n-value", StringComparison.Ordinal) == true);
        Assert.Equal(string.Empty, scope.Environment["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("credential.helper", scope.Environment["GIT_CONFIG_KEY_0"]);
    }

    [Fact]
    public void Deploy_keys_and_known_hosts_are_private_files_removed_with_the_scope()
    {
        if (OperatingSystem.IsWindows()) return;
        string directory;
        using (GitAuthenticationScope scope = GitAuthenticationScope.Create(new GitAuthentication(
                   Ssh: new GitSshCredential("-----BEGIN OPENSSH PRIVATE KEY-----\nx\n-----END OPENSSH PRIVATE KEY-----"),
                   HostTrust: new GitSshHostTrust("git.example.com", 22, [], true))))
        {
            directory = scope.DirectoryPath!;
            var identity = Path.Combine(directory, "identity");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(identity));
            var ssh = scope.Environment["GIT_SSH_COMMAND"]!;
            Assert.Contains("StrictHostKeyChecking=accept-new", ssh, StringComparison.Ordinal);
            Assert.Contains("IdentitiesOnly=yes", ssh, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE KEY", ssh, StringComparison.Ordinal);
        }

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Pinned_hosts_are_enforced_strictly()
    {
        using GitAuthenticationScope scope = GitAuthenticationScope.Create(new GitAuthentication(
            HostTrust: new GitSshHostTrust("github.com", 22, GitHostKeys.PublishedFor("github.com", 22), true)));

        Assert.Contains("StrictHostKeyChecking=yes", scope.Environment["GIT_SSH_COMMAND"], StringComparison.Ordinal);
        Assert.Equal(3, File.ReadAllLines(Path.Combine(scope.DirectoryPath!, "known_hosts")).Length);
    }

    [Fact]
    public void Anonymous_operations_need_no_scope_directory()
    {
        using GitAuthenticationScope scope = GitAuthenticationScope.Create(GitAuthentication.None);

        Assert.Null(scope.DirectoryPath);
        Assert.Empty(scope.Environment);
    }

    [Fact]
    public void GitHub_app_jwt_is_rs256_signed_and_short_lived()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var jwt = GitHubAppJwt.Create(4242, pem, now);

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64Url(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using JsonDocument payload = JsonDocument.Parse(Base64Url(parts[1]));
        Assert.Equal("4242", payload.RootElement.GetProperty("iss").ToString());
        var lifetime = payload.RootElement.GetProperty("exp").GetInt64() - payload.RootElement.GetProperty("iat").GetInt64();
        Assert.InRange(lifetime, 1, 600);
    }

    private async Task<string?> AskAsync(GitAuthenticationScope scope, string prompt)
    {
        ProcessResult result = await _runner.RunTextAsync(new ProcessCommand("sh",
            [scope.Environment["GIT_ASKPASS"]!, prompt], Environment: scope.Environment,
            Timeout: TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);
        return result.Succeeded ? result.StandardOutput.TrimEnd('\n') : null;
    }

    private static byte[] Base64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
