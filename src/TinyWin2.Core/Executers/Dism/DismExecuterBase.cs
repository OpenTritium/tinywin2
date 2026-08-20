using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>Shared plumbing for dism.exe-backed executers against the mounted image.</summary>
public abstract class DismExecuterBase(IProcessRunner runner) {
    protected IProcessRunner Runner { get; } = runner;

    /// <summary>
    /// Runs dism.exe with /English (stable output keys regardless of host display language);
    /// never throws on non-zero — callers classify via <see cref="DismErrors"/>.
    /// </summary>
    protected async Task<(int ExitCode, string Output)> RunDismAsync(
        ExecContext context,
        IReadOnlyList<string> arguments,
        CancellationToken ct) {
        var fullArgs = new List<string> { $"/Image:{context.MountPath}", "/English" };
        fullArgs.AddRange(arguments);
        context.Log.Debug($"dism.exe {string.Join(" ", fullArgs)}");
        var result = await Runner.RunAsync("dism.exe", fullArgs,
            new ProcessRunOptions { IgnoreExitCode = true, OnOutputLine = null }, ct);
        return (result.ExitCode, result.Output + result.Error);
    }

    protected static IReadOnlyList<Dictionary<string, string>> ParseList(string output) => DismListParser.Parse(output);
}
