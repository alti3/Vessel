using Vessel.Application.Processes;
using Vessel.Application.Terminals;
using Vessel.Domain;
using Vessel.Domain.Servers;
using Vessel.Domain.Terminals;
using Vessel.Infrastructure.Processes;
using Vessel.Infrastructure.Security;

namespace Vessel.IntegrationTests.Processes;

/// <summary>SRV-01: terminals never inherit the daemon environment and never run for remote targets.</summary>
public sealed class TerminalEnvironmentIsolationTests
{
    private const string SecretVariable = "Secrets__MasterKey";

    [Fact]
    public async Task ProcessRunner_WithInheritEnvironmentFalse_DoesNotLeakParentVariables()
    {
        if (OperatingSystem.IsWindows()) return;
        Environment.SetEnvironmentVariable("VESSEL_TEST_DAEMON_SECRET", "leaked-value");
        try
        {
            var runner = new DotNetProcessRunner(new SecretRedactor(), TimeProvider.System);
            ProcessResult isolated = await runner.RunTextAsync(new ProcessCommand("/bin/sh", ["-c", "env"],
                Environment: new Dictionary<string, string?> { ["ONLY"] = "1" },
                Timeout: TimeSpan.FromSeconds(10), InheritEnvironment: false), TestContext.Current.CancellationToken);
            ProcessResult inherited = await runner.RunTextAsync(new ProcessCommand("/bin/sh", ["-c", "env"],
                Timeout: TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);

            Assert.DoesNotContain("VESSEL_TEST_DAEMON_SECRET", isolated.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("ONLY=1", isolated.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("VESSEL_TEST_DAEMON_SECRET", inherited.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VESSEL_TEST_DAEMON_SECRET", null);
        }
    }

    [Fact]
    public async Task TerminalBridge_StartsShellWithExplicitMinimalEnvironment()
    {
        Environment.SetEnvironmentVariable(SecretVariable, "do-not-leak");
        try
        {
            var runner = new CapturingProcessRunner();
            await using var bridge = new InteractiveTerminalProcessBridge(runner);

            await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.OpenAsync(Request(ServerConnectionType.Local),
                (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask, TestContext.Current.CancellationToken));

            ProcessCommand command = Assert.Single(runner.Commands);
            Assert.False(command.InheritEnvironment);
            Assert.NotNull(command.Environment);
            Assert.DoesNotContain(SecretVariable, command.Environment.Keys);
            Assert.DoesNotContain(command.Environment.Values, value => value == "do-not-leak");
            Assert.All(command.Environment.Keys, key =>
                Assert.True(key is "TERM" or "LANG" || InteractiveTerminalProcessBridge.AllowedInheritedVariables.Contains(key),
                    $"Unexpected terminal environment variable '{key}'."));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretVariable, null);
        }
    }

    [Fact]
    public async Task TerminalBridge_RejectsRemoteTargetsWithoutStartingAProcess()
    {
        var runner = new CapturingProcessRunner();
        await using var bridge = new InteractiveTerminalProcessBridge(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.OpenAsync(Request(ServerConnectionType.Ssh),
            (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask, TestContext.Current.CancellationToken));

        Assert.Empty(runner.Commands);
    }

    private static TerminalBridgeOpenRequest Request(ServerConnectionType connection) =>
        new(TerminalSessionId.New(), ServerId.New(), TerminalTargetType.Server, null,
            TerminalSecurityPolicy.Default.DefaultShellCommand, 80, 24, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1),
            connection);

    /// <summary>Records commands and refuses to start them, so no real shell is spawned.</summary>
    private sealed class CapturingProcessRunner : IProcessRunner
    {
        public List<ProcessCommand> Commands { get; } = [];

        public Task<IInteractiveProcess> StartInteractiveAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            throw new InvalidOperationException("captured");
        }

        public Task<ProcessResult> RunTextAsync(ProcessCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProcessExitInfo> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProcessBinaryResult> RunBinaryAsync(ProcessCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProcessResult> RunTextWithInputAsync(ProcessCommand command, Stream standardInput, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ProcessOutputLine> StreamLinesAsync(ProcessCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
