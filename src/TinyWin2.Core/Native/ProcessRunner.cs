using System.Diagnostics;
using System.Text;

namespace TinyWin2.Core.Native;

/// <summary>Outcome of one native tool invocation.</summary>
public sealed record ProcessRunResult(int ExitCode, string Output, string Error, string CommandLine) {
    public bool Success => ExitCode == 0;
}

/// <summary>Options for a native tool invocation.</summary>
public sealed class ProcessRunOptions {
    public string? WorkingDirectory { get; init; }
    public TimeSpan? Timeout { get; init; }
    /// <summary>When false (default), a non-zero exit code throws <see cref="ProcessRunnerException"/>.</summary>
    public bool IgnoreExitCode { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public Action<string>? OnOutputLine { get; init; }
    public Action<string>? OnErrorLine { get; init; }
}

/// <summary>Thrown when a native tool exits non-zero (and exit codes were not ignored).</summary>
public sealed class ProcessRunnerException(
    string fileName,
    ProcessRunResult result) : Exception(
        $"'{fileName}' exited with code {result.ExitCode}.{Environment.NewLine}Command line: {result.CommandLine}{Environment.NewLine}{result.Output}{Environment.NewLine}{result.Error}") {
    public ProcessRunResult Result { get; } = result;
}

public interface IProcessRunner {
    Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs external tools (dism.exe, reg.exe, diskpart, oscdimg, ...) with streamed output capture,
/// timeout and cancellation support. The single boundary between the engine and native tooling.
/// </summary>
public sealed class ProcessRunner : IProcessRunner {
    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default) {
        options ??= new ProcessRunOptions();
        var startInfo = new ProcessStartInfo {
            FileName = fileName,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // stdin closed below: some tools wait on it
            CreateNoWindow = true,
        };
        if (options.Environment is not null) {
            foreach (var (key, value) in options.Environment) {
                startInfo.Environment[key] = value;
            }
        }
        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }
        var commandLine = BuildCommandLineEcho(fileName, arguments);
        using var process = new Process { StartInfo = startInfo };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) => {
            if (e.Data is null) {
                return;
            }
            lock (outputBuilder) {
                outputBuilder.AppendLine(e.Data);
            }
            options.OnOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) => {
            if (e.Data is null) {
                return;
            }
            lock (errorBuilder) {
                errorBuilder.AppendLine(e.Data);
            }
            options.OnErrorLine?.Invoke(e.Data);
        };
        if (!process.Start()) {
            throw new ProcessRunnerException(fileName,
                new ProcessRunResult(-1, "", $"Failed to start '{fileName}'.", commandLine));
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var timeout = options.Timeout ?? Timeout.InfiniteTimeSpan;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
            KillTree(process);
            throw new TimeoutException($"'{fileName}' timed out after {timeout}. Command line: {commandLine}");
        }
        catch (OperationCanceledException) {
            KillTree(process);
            throw;
        }
        string output;
        string error;
        lock (outputBuilder) {
            output = outputBuilder.ToString();
        }
        lock (errorBuilder) {
            error = errorBuilder.ToString();
        }
        var result = new ProcessRunResult(process.ExitCode, output, error, commandLine);
        if (process.ExitCode != 0 && !options.IgnoreExitCode) {
            throw new ProcessRunnerException(fileName, result);
        }
        return result;
    }

    private static void KillTree(Process process) {
        try {
            process.Kill(entireProcessTree: true);
        }
        catch {
            // The process may already have exited between the cancellation and the kill.
        }
    }

    private static string BuildCommandLineEcho(string fileName, IReadOnlyList<string> arguments)
        => fileName + (arguments.Count == 0 ? "" : " " + string.Join(" ", arguments.Select(QuoteIfNeeded)));

    internal static string QuoteIfNeeded(string value)
        => value.Length != 0 && !value.Contains(' ') && !value.Contains('"')
            ? value
            : $"\"{value.Replace("\"", "\\\"")}\"";
}
