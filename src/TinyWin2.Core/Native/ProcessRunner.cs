using System.Diagnostics;
using System.Text;

namespace TinyWin2.Core.Native;

public sealed record ProcessRunResult(int ExitCode, string Output, string Error, string CommandLine) {
    public bool Success => ExitCode == 0;
}

public sealed class ProcessRunOptions {
    public TimeSpan? Timeout { get; init; }
    public bool IgnoreExitCode { get; init; }
    public Action<string>? OnOutputLine { get; init; }
    public int MaxOutputCharacters { get; init; } = 4 * 1024 * 1024;
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
///     Runs external tools (dism.exe, reg.exe, diskpart, oscdimg, ...) with streamed output capture,
///     timeout and cancellation support. The single boundary between the engine and native tooling.
/// </summary>
public sealed class ProcessRunner : IProcessRunner {
    private static readonly TimeSpan TerminationWait = TimeSpan.FromSeconds(5);

    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default) {
        options ??= new();
        if (options.MaxOutputCharacters < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(options), "MaxOutputCharacters cannot be negative.");
        }

        var startInfo = new ProcessStartInfo {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // stdin closed below: some tools wait on it
            CreateNoWindow = true
        };
        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }

        var commandLine = BuildCommandLineEcho(fileName, arguments);
        // Assign StartInfo after the using declaration: an exception in an object
        // initializer would leak a constructed Process outside using's scope.
        using var process = new Process();
        process.StartInfo = startInfo;
        var outputBuilder = new OutputBuffer(options.MaxOutputCharacters);
        var errorBuilder = new OutputBuffer(options.MaxOutputCharacters);
        if (!process.Start()) {
            throw new ProcessRunnerException(fileName,
                new(-1, "", $"Failed to start '{fileName}'.", commandLine));
        }

        process.StandardInput.Close();
        var timeout = options.Timeout ?? Timeout.InfiniteTimeSpan;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var outputTask = ReadStreamAsync(process.StandardOutput, outputBuilder, linked.Token, options.OnOutputLine);
        var errorTask = ReadStreamAsync(process.StandardError, errorBuilder, linked.Token);
        var processExitTask = process.WaitForExitAsync(linked.Token);
        try {
            // WaitAsync returns promptly on cancellation; the linked token also cancels stream
            // reads. Normal completion still waits for both streams to reach EOF.
            await WaitForCompletionAsync(processExitTask, outputTask, errorTask, linked.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested) {
            CancelStreamReads(linked);
            var cleanupError = await StopProcessAsync(process, outputTask, errorTask);
            var timeoutError = new TimeoutException(
                $"'{fileName}' timed out after {timeout}. Command line: {commandLine}");
            if (cleanupError is not null) {
                throw new AggregateException("Native process cleanup failed.", timeoutError, cleanupError);
            }

            throw timeoutError;
        }
        catch (OperationCanceledException ex) {
            CancelStreamReads(linked);
            var cleanupError = await StopProcessAsync(process, outputTask, errorTask);
            if (cleanupError is not null) {
                throw new AggregateException("Native process cleanup failed.", ex, cleanupError);
            }

            throw;
        }
        catch (Exception ex) {
            CancelStreamReads(linked);
            var cleanupError = await StopProcessAsync(process, outputTask, errorTask);
            if (cleanupError is not null) {
                throw new AggregateException("Native process cleanup failed.", ex, cleanupError);
            }

            throw;
        }

        var result = new ProcessRunResult(process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString(),
            commandLine);
        if (process.ExitCode != 0 && !options.IgnoreExitCode) {
            throw new ProcessRunnerException(fileName, result);
        }

        return result;
    }

    /// <summary>Drains a redirected stream line by line until EOF; single writer, no locking needed.</summary>
    private static async Task
        ReadStreamAsync(StreamReader reader, OutputBuffer buffer, CancellationToken ct,
            Action<string>? onLine = null) {
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line) {
            buffer.AppendLine(line);
            onLine?.Invoke(line);
        }
    }

    private static async Task WaitForCompletionAsync(
        Task processExitTask,
        Task outputTask,
        Task errorTask,
        CancellationToken ct) {
        var pending = new List<Task> { processExitTask, outputTask, errorTask };
        while (pending.Count > 0) {
            var completed = await Task.WhenAny(pending).WaitAsync(ct).ConfigureAwait(false);
            pending.Remove(completed);
            await completed.ConfigureAwait(false);
        }
    }

    private static async Task<Exception?> StopProcessAsync(
        Process process,
        Task outputTask,
        Task errorTask) {
        Exception? killError = null;
        try {
            process.Kill(true);
        }
        catch (Exception ex) {
            // The process may have exited between cancellation and cleanup.
            killError = ex;
        }

        var processExited = false;
        try {
            await process.WaitForExitAsync().WaitAsync(TerminationWait).ConfigureAwait(false);
            processExited = true;
        }
        catch (TimeoutException) {
            // Return a cleanup error below; the child may still be running.
        }
        catch {
            // Observe a failed wait task; the original operation exception wins.
        }

        try {
            await Task.WhenAll(outputTask, errorTask).WaitAsync(TerminationWait).ConfigureAwait(false);
        }
        catch {
            // Observe reader failures and do not mask the original operation exception.
        }

        if (!processExited) {
            return new TimeoutException(
                $"Native process '{process.StartInfo.FileName}' did not terminate within {TerminationWait}.");
        }

        return killError is not null && !HasExited(process)
            ? new InvalidOperationException(
                $"Failed to terminate native process '{process.StartInfo.FileName}'.", killError)
            : null;
    }

    private static void CancelStreamReads(CancellationTokenSource linked) {
        try {
            linked.Cancel();
        }
        catch {
            // Cleanup must not replace the original failure.
        }
    }

    private static bool HasExited(Process process) {
        try {
            return process.HasExited;
        }
        catch {
            return false;
        }
    }

    private static string BuildCommandLineEcho(string fileName, IReadOnlyList<string> arguments)
        => fileName + (arguments.Count == 0 ? "" : " " + string.Join(" ", arguments.Select(QuoteIfNeeded)));

    internal static string QuoteIfNeeded(string value)
        => value.Length != 0 && !value.Contains(' ') && !value.Contains('\t') && !value.Contains('"')
            ? value
            : $"\"{value.Replace("\"", "\\\"")}\"";

    private sealed class OutputBuffer(int maxCharacters) {
        private const string TruncatedMarker = "...[output truncated]";
        private readonly StringBuilder _builder = new();
        private bool _truncated;

        public void AppendLine(string line) {
            if (_truncated) {
                return;
            }

            var remaining = maxCharacters - _builder.Length;
            if (remaining <= 0) {
                _truncated = true;
                return;
            }

            var renderedLength = line.Length + Environment.NewLine.Length;
            if (renderedLength <= remaining) {
                _builder.AppendLine(line);
                return;
            }

            var markerLength = Math.Min(TruncatedMarker.Length, remaining);
            var contentLength = Math.Max(0, remaining - markerLength);
            if (contentLength > 0) {
                _builder.Append(line.AsSpan(0, Math.Min(contentLength, line.Length)));
            }

            _builder.Append(TruncatedMarker.AsSpan(0, markerLength));
            _truncated = true;
        }

        public override string ToString() => _builder.ToString();
    }
}
