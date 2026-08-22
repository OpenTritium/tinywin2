using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using TinyWin2.Core.Plans;

namespace TinyWin2.Gui.Services;

/// <summary>Locates the repo root (plans/ directory) and the tinywin2 CLI executable.</summary>
public static class RepositoryLocator {
    public static string FindPlansDirectory() =>
        PlansDirectoryLocator.TryLocate()
        ?? throw new DirectoryNotFoundException("未找到 plans 目录（请从仓库内启动 GUI）.");

    public static string FindCliExecutable() {
        // Same output directory first (published layout), then CLI build output beside us.
        var beside = Path.Combine(AppContext.BaseDirectory, "tinywin2.exe");
        if (File.Exists(beside)) {
            return beside;
        }

        var parent = Directory.GetParent(AppContext.BaseDirectory);
        while (parent is not null) {
            var candidate = Path.Combine(parent.FullName, "TinyWin2.Cli", "bin", "Debug", "net10.0", "tinywin2.exe");
            if (File.Exists(candidate)) {
                return candidate;
            }

            // Release layout
            candidate = Path.Combine(parent.FullName, "TinyWin2.Cli", "bin", "Release", "net10.0", "tinywin2.exe");
            if (File.Exists(candidate)) {
                return candidate;
            }

            parent = parent.Parent;
        }

        throw new FileNotFoundException("未找到 tinywin2.exe（先构建 CLI: dotnet build src/TinyWin2.Cli）.");
    }
}

/// <summary>Runs the CLI in --json-events mode and streams events back on the UI thread.</summary>
public sealed class CliRunner {
    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        Action<JsonObject> onEvent,
        Action<string> onRawLine,
        Action<Exception> onFailed,
        CancellationToken cancellationToken) {
        var cli = RepositoryLocator.FindCliExecutable();
        var startInfo = new ProcessStartInfo {
            FileName = cli,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process();
        process.StartInfo = startInfo;
        var queue = Channel.CreateUnbounded<string>();
        process.OutputDataReceived += (_, e) => {
            if (e.Data is not null) {
                queue.Writer.TryWrite(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) => {
            if (e.Data is not null) {
                queue.Writer.TryWrite(e.Data);
            }
        };
        try {
            if (!process.Start()) {
                throw new InvalidOperationException("无法启动 tinywin2.exe");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var reading = Task.Run(async () => {
                await foreach (var line in queue.Reader.ReadAllAsync(cancellationToken)) {
                    onRawLine(line);
                    JsonObject? evt = null;
                    try {
                        evt = JsonNode.Parse(line) as JsonObject;
                    }
                    catch {
                        // non-JSON line (rare): already surfaced via onRawLine
                    }

                    if (evt is not null && (evt.ContainsKey("phase") || evt.ContainsKey("seq"))) {
                        onEvent(evt);
                    }
                }
            }, cancellationToken);
            try {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException) {
                try {
                    process.Kill(true);
                }
                catch {
                    /* raced */
                }

                throw;
            }

            queue.Writer.TryComplete();
            await reading;
            return process.ExitCode;
        }
        catch (Exception ex) {
            onFailed(ex);
            return -1;
        }
    }
}
