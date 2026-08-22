using TinyWin2.Core.Native;

namespace TinyWin2.Core.Tests;

/// <summary>Scriptable IProcessRunner: records every call, answers from a handler.</summary>
public sealed class FakeProcessRunner : IProcessRunner {
    public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];
    public Func<string, IReadOnlyList<string>, ProcessRunResult>? Handler { get; set; }

    public Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default) {
        Calls.Add((fileName, arguments));
        options?.OnOutputLine?.Invoke("");
        if (Handler is null) {
            return Task.FromResult(Ok());
        }

        var result = Handler(fileName, arguments);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            options?.OnOutputLine?.Invoke(line.TrimEnd('\r'));
        }

        return Task.FromResult(result);
    }

    public static ProcessRunResult Ok(string output = "") => new(0, output, "", $"{output}");
    public static ProcessRunResult Fail(int exitCode, string output = "") => new(exitCode, output, "", $"{output}");

    public IReadOnlyList<string> ArgsOf(int index) => Calls[index].Args;
    public bool Called(string fileName) => Calls.Any(c => c.File == fileName);
}
