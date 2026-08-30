using System.Diagnostics;
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
        var ex = Assert.Throws<ProcessRunnerException>(() =>
            runner.RunAsync(exe, args, new() { Timeout = TimeSpan.FromSeconds(10) }).GetAwaiter().GetResult());
        await Assert.That(ex.Result.ExitCode).IsEqualTo(3);
    }

    [Test]
    public async Task IgnoreExitCodeReturnsResult() {
        var runner = new ProcessRunner();
        var (exe, args) = SplitCommand(FailCommand());
        var result = await runner.RunAsync(exe, args, new() { IgnoreExitCode = true });
        await Assert.That(result.ExitCode).IsEqualTo(3);
    }

    [Test]
    public async Task BoundsCapturedOutput() {
        var runner = new ProcessRunner();
        var exe = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        IReadOnlyList<string> args = OperatingSystem.IsWindows()
            ? ["/c", "for /L %i in (1,1,20) do @echo 1234567890"]
            : ["-c", "yes 1234567890 | head -n 20"];

        var result = await runner.RunAsync(exe, args,
            new() { MaxOutputCharacters = 150 });

        await Assert.That(result.Output.Length).IsLessThanOrEqualTo(150);
        await Assert.That(result.Output).Contains("...[");
    }

    [Test]
    public async Task TimeoutKillsTheProcess() {
        var runner = new ProcessRunner();
        // `pause` returns immediately once stdin closes; ping actually blocks for ~30s.
        var hangCommand = OperatingSystem.IsWindows()
            ? "/c ping -n 30 127.0.0.1 > nul"
            : "-c 'sleep 30'";
        var (exe, args) = SplitCommand(hangCommand);
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            runner.RunAsync(exe, args, new() { Timeout = TimeSpan.FromSeconds(1) }));
        stopwatch.Stop();
        await Assert.That(stopwatch.Elapsed.TotalSeconds).IsLessThan(5);
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
