using TinyWin2.Core.Native;

namespace TinyWin2.Core.Tests;

public sealed class ProcessRunnerTests {
    private static string Shell => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    private static string EchoCommand(string text) =>
        OperatingSystem.IsWindows() ? $"/c echo {text}" : $"-c 'echo {text}'";

    private static string FailCommand() =>
        OperatingSystem.IsWindows() ? "/c exit 3" : "-c 'exit 3'";

    [Test]
    public async Task CapturesOutputAndExitCode() {
        var runner = new ProcessRunner();
        var (exe, args) = SplitCommand(EchoCommand("hello-tinywin"));
        var result = await runner.RunAsync(exe, args);
        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output.Trim()).IsEqualTo("hello-tinywin");
        await Assert.That(result.CommandLine).Contains(exe);
    }

    [Test]
    public async Task NonZeroExitThrowsWithOutput() {
        var runner = new ProcessRunner();
        var (exe, args) = SplitCommand(FailCommand());
        var ex = Assert.Throws<ProcessRunnerException>(
            () => runner.RunAsync(exe, args, new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(10) }).GetAwaiter().GetResult());
        await Assert.That(ex.Result.ExitCode).IsEqualTo(3);
    }

    [Test]
    public async Task IgnoreExitCodeReturnsResult() {
        var runner = new ProcessRunner();
        var (exe, args) = SplitCommand(FailCommand());
        var result = await runner.RunAsync(exe, args, new ProcessRunOptions { IgnoreExitCode = true });
        await Assert.That(result.ExitCode).IsEqualTo(3);
    }

    [Test]
    public async Task StreamsOutputLinesToCallback() {
        var runner = new ProcessRunner();
        var lines = new List<string>();
        var (exe, args) = SplitCommand(EchoCommand("streamed"));
        await runner.RunAsync(exe, args, new ProcessRunOptions { OnOutputLine = lines.Add });
        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0].Trim()).IsEqualTo("streamed");
    }

    [Test]
    public async Task TimeoutKillsTheProcess() {
        var runner = new ProcessRunner();
        // `pause` returns immediately once stdin closes; ping actually blocks for ~30s.
        var hangCommand = OperatingSystem.IsWindows()
            ? "/c ping -n 30 127.0.0.1 > nul"
            : "-c 'sleep 30'";
        var (exe, args) = SplitCommand(hangCommand);
        await Assert.ThrowsAsync<TimeoutException>(
            () => runner.RunAsync(exe, args, new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(1) }));
    }

    [Test]
    public async Task QuotesArgumentsWithSpacesInEcho() {
        await Assert.That(ProcessRunner.QuoteIfNeeded("plain")).IsEqualTo("plain");
        await Assert.That(ProcessRunner.QuoteIfNeeded("with space")).IsEqualTo("\"with space\"");
    }

    private static (string Exe, string[] Args) SplitCommand(string command) {
        var parts = command.Split(' ');
        return (Shell, [.. parts]);
    }
}
