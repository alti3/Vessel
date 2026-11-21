using Vessel.Application.Ssh;
using Vessel.Domain.Servers;

namespace Vessel.UnitTests.Ssh;

/// <summary>SRV-02/SEC-12: strict key acceptance, OpenSSH fingerprints, host key normalization.</summary>
public sealed class SshKeyFormatsTests
{
    public const string Ed25519Public =
        "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKZiUHx44cxJLcRoTdJpjpyCL1MQuSVAtW43jjLUSf8s test@vessel";

    public const string Ed25519Fingerprint = "SHA256:3Lya1DYh8j0dVRzFThUyqaCjLdBDaZG5+bYD6HJdOcM";

    public const string Rsa1024Public =
        "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAAAgQC3SjlIn9L/Z9ZhO1ZAhov7xCn+/FxNXtUOnzBeMcdg/DYKdgThXufEugTUoC1d/+5+2cuSaRPlcOITLXqN2mUBRG8TggI61L2V2WMGCOJYrODprkF00+/4vZf9RtDlOikuAt7cPxfMtJCF4CL2W55rq7KYTquHV5HMRmKrxLxmvw== small";

    public const string Rsa2048Public =
        "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQCxAf6rZk51GnfbKA5GBTVjd9lhMyvVrT6P21HikH2BRufzeBCJzpvsSTQ4aOqIUVDhLBDKL9ovhm3OmZGazY3919s25xV4sq6IulCGxwPCzYYWzt9oxT/Yavy72lYndyB5kv37ifvx/5LtVRUNdrLvlw2ESFTYDaVsFMS7RarYWOVxqu4emNv+m1H7i/1gx+A8hmRPauq9yVREc26rAr1wwvS9Xz1s/X/SNdsZqVQgnAH4b+SWQ+s3XdEmC5ErIMCQPzOBX7A5iBOJ2DpwVAhMxm2NIn+XOUILwaF621GKFL872G8j2+cO2kNvp5RvEMudClCGZeGMBxPqasitjeFj big";

    public const string Rsa2048Fingerprint = "SHA256:5OOvMn17bOa2v1/UEEYFkQArHA4RhhrqGSQm5Ishh7E";

    public const string Ecdsa384Public =
        "ecdsa-sha2-nistp384 AAAAE2VjZHNhLXNoYTItbmlzdHAzODQAAAAIbmlzdHAzODQAAABhBDk7rSjAzKO4Kar8T3/iGApU9PwW3mJja59aBS/9ckNgN9cF+KSLThBP5QpX9i01xtphzdLJwu7gdOz+KBFlEdL+4B6iED3TJ9lMpTPUQOl3s9JzOedRObQi7PY9k38xjQ== ec";

    [Theory]
    [InlineData("PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: none", "PuTTY")]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKZiUHx44cxJ", "PuTTY")]
    [InlineData("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----", "not a public key")]
    [InlineData("-----BEGIN DSA PRIVATE KEY-----\nAAAA\n-----END DSA PRIVATE KEY-----", "DSA")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nAAAA\n-----END RSA PRIVATE KEY-----", "Encrypted")]
    [InlineData("-----BEGIN ENCRYPTED PRIVATE KEY-----\nAAAA\n-----END ENCRYPTED PRIVATE KEY-----", "Encrypted")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n-----BEGIN OPENSSH PRIVATE KEY-----\nBBBB\n-----END OPENSSH PRIVATE KEY-----", "exactly one")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END RSA PRIVATE KEY-----", "exactly one")]
    [InlineData("", "required")]
    public void ValidatePrivateKeyText_RejectsUnsupportedFormats(string key, string expected)
    {
        var error = SshKeyFormats.ValidatePrivateKeyText(key);

        Assert.NotNull(error);
        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidatePrivateKeyText_AcceptsSingleOpenSshBlock()
    {
        Assert.Null(SshKeyFormats.ValidatePrivateKeyText(
            "-----BEGIN OPENSSH PRIVATE KEY-----\r\nb3BlbnNzaC1rZXktdjEAAAAA\r\n-----END OPENSSH PRIVATE KEY-----\r\n"));
        Assert.Null(SshKeyFormats.ValidatePrivateKeyText(new string('x', 0) +
                                                         "-----BEGIN RSA PRIVATE KEY-----\nAAAA\n-----END RSA PRIVATE KEY-----"));
    }

    [Fact]
    public void ValidatePrivateKeyText_RejectsOversizedKeys()
    {
        var key = "-----BEGIN OPENSSH PRIVATE KEY-----\n" + new string('A', SshKeyFormats.MaxPrivateKeyLength) +
                  "\n-----END OPENSSH PRIVATE KEY-----";
        Assert.Contains("too large", SshKeyFormats.ValidatePrivateKeyText(key));
    }

    [Theory]
    [InlineData(Ed25519Public, "ssh-ed25519", 256, Ed25519Fingerprint)]
    [InlineData(Rsa2048Public, "ssh-rsa", 2048, Rsa2048Fingerprint)]
    public void ParsePublicKey_ReadsTypeBitsAndOpenSshFingerprint(string line, string algorithm, int bits,
        string fingerprint)
    {
        SshPublicKeyInfo key = SshKeyFormats.ParsePublicKey(line);

        Assert.Equal(algorithm, key.Algorithm);
        Assert.Equal(bits, key.Bits);
        Assert.Equal(fingerprint, key.Fingerprint);
        Assert.Null(SshKeyFormats.ValidatePublicKeyPolicy(key));
    }

    [Fact]
    public void ValidatePublicKeyPolicy_RejectsShortRsaKeys()
    {
        SshPublicKeyInfo key = SshKeyFormats.ParsePublicKey(Rsa1024Public);

        Assert.Equal(1024, key.Bits);
        Assert.Contains("2048", SshKeyFormats.ValidatePublicKeyPolicy(key));
    }

    [Fact]
    public void ParsePublicKey_RejectsTypeThatDoesNotMatchTheBlob()
    {
        var forged = "ssh-rsa " + Ed25519Public.Split(' ')[1];

        Assert.Throws<FormatException>(() => SshKeyFormats.ParsePublicKey(forged));
        Assert.Throws<FormatException>(() => SshKeyFormats.ParsePublicKey("ssh-ed25519 not*base64"));
    }

    [Fact]
    public void NormalizeHostKeys_DropsHostNamesMarkersCommentsAndInvalidLines()
    {
        var keyBlob = Ed25519Public.Split(' ')[1];
        var input = $"""
                     # comment
                     vessel-server-abc {Ed25519Public}
                     @revoked * {Rsa2048Public}
                     |1|hashed= ssh-rsa notbase64!!
                     {Ecdsa384Public}
                     vessel-server-abc ssh-ed25519 {keyBlob}
                     """;

        var normalized = SshKeyFormats.NormalizeHostKeys(input);

        Assert.NotNull(normalized);
        var lines = normalized.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains($"ssh-ed25519 {keyBlob}", lines);
        Assert.Contains(lines, line => line.StartsWith("ecdsa-sha2-nistp384 ", StringComparison.Ordinal));
        Assert.Null(SshKeyFormats.NormalizeHostKeys("garbage\n# nothing"));
    }

    [Fact]
    public void KnownHostsContent_UsesTheVesselAlias()
    {
        var content = SshKeyFormats.KnownHostsContent("vessel-server-1", "ssh-ed25519 AAAA\nssh-rsa BBBB");

        Assert.Equal("vessel-server-1 ssh-ed25519 AAAA\nvessel-server-1 ssh-rsa BBBB\n", content);
    }

    [Theory]
    [InlineData("@@@ WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED! @@@", true)]
    [InlineData("Host key verification failed.", true)]
    [InlineData("Permission denied (publickey).", false)]
    [InlineData(null, false)]
    public void IsHostKeyMismatch_DetectsOpenSshMessages(string? stderr, bool expected)
    {
        Assert.Equal(expected, SshKeyFormats.IsHostKeyMismatch(stderr));
    }

    [Theory]
    [InlineData("enforce", SshHostKeyCheckingMode.Enforce, SshHostKeyCheckingMode.Warn, SshHostKeyCheckingMode.Warn)]
    [InlineData("warn", SshHostKeyCheckingMode.Warn, SshHostKeyCheckingMode.Enforce, SshHostKeyCheckingMode.Enforce)]
    [InlineData("OFF", SshHostKeyCheckingMode.Off, SshHostKeyCheckingMode.Enforce, SshHostKeyCheckingMode.Off)]
    [InlineData("typo", SshHostKeyCheckingMode.Enforce, SshHostKeyCheckingMode.Enforce, SshHostKeyCheckingMode.Enforce)]
    public void HostKeyCheckingMode_DefaultsToEnforceAndInstanceOffDisables(string configured,
        SshHostKeyCheckingMode instance, SshHostKeyCheckingMode server, SshHostKeyCheckingMode effective)
    {
        var options = new SshOptions { HostKeyChecking = configured };

        Assert.Equal(instance, options.ResolveInstanceMode());
        Assert.Equal(effective, SshOptions.Effective(options.ResolveInstanceMode(), server));
        Assert.Equal(SshHostKeyCheckingMode.Enforce, new SshOptions().ResolveInstanceMode());
    }
}
