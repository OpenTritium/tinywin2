using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Runtime.ExceptionServices;
using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Layers;

/// <summary>
/// Per-layer post-mortem evidence, captured while the layer is still attached:
/// a full file manifest and semantic registry exports. This makes layer diffing
/// independent of re-attaching the differencing chain (which some Windows builds
/// reject after the build finished) and avoids polluting layers on remount.
/// </summary>
public static partial class LayerEvidence {
    private const string SnapshotsDirectoryName = "snapshots";

    public static string SnapshotsRoot(string workDirectory) => Path.Combine(workDirectory, SnapshotsDirectoryName);

    public static string ManifestPathFor(string snapshotsRoot, int index) =>
        Path.Combine(snapshotsRoot, $"L{index:D3}.files.tsv");

    public static string RegistryPathFor(string snapshotsRoot, int index) =>
        Path.Combine(snapshotsRoot, $"L{index:D3}.registry.reg");

    public sealed record FileManifestSnapshot(
        Dictionary<string, (long Size, long WriteTicks)> Entries,
        bool Complete);

    /// <summary>Captures evidence for one layer (index 0 = base). Must run while the layer is attached.</summary>
    public static async Task CaptureAsync(
        string mountPath,
        string workDirectory,
        int index,
        IProcessRunner runner,
        BuildLog log,
        CancellationToken ct) {
        var snapshotsRoot = SnapshotsRoot(workDirectory);
        Directory.CreateDirectory(snapshotsRoot);
        await CaptureFileManifestAsync(mountPath, ManifestPathFor(snapshotsRoot, index), log, ct);
        await CaptureRegistryAsync(mountPath, RegistryPathFor(snapshotsRoot, index), runner, log, ct);
        log.Debug($"captured layer {index:000} evidence snapshots", layerIndex: index);
    }

    /// <summary>Jump-safe full-tree manifest: size \t mtimeUtc \t relativePath.</summary>
    private static async Task CaptureFileManifestAsync(string mountPath, string outputFile, BuildLog log,
        CancellationToken ct) {
        var builder = new StringBuilder(1 << 20);
        var complete = Enumerate(mountPath.TrimEnd('\\') + "\\", "",
            (size, writeUtc, relative) => {
                builder.Append(size).Append('\t').Append(writeUtc.Ticks).Append('\t').Append(relative).Append('\n');
            }, log, ct);
        var header = complete ? "# tinywin2-files-v1 complete\n" : "# tinywin2-files-v1 incomplete\n";
        await File.WriteAllTextAsync(outputFile, header + builder, ct);
    }

    /// <summary>Loads a manifest snapshot: relativePath → (size, writeTicks).</summary>
    public static Dictionary<string, (long Size, long WriteTicks)> LoadManifest(string path) =>
        LoadManifestSnapshot(path).Entries;

    public static FileManifestSnapshot LoadManifestSnapshot(string path) {
        var result = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) {
            throw new FileNotFoundException($"evidence manifest not found: {path}", path);
        }

        var complete = true;
        var headerSeen = false;
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path)) {
            lineNumber++;
            if (line.StartsWith('#')) {
                if (line.Equals("# tinywin2-files-v1 incomplete", StringComparison.Ordinal)) {
                    complete = false;
                    headerSeen = true;
                }
                else if (line.Equals("# tinywin2-files-v1 complete", StringComparison.Ordinal)) {
                    headerSeen = true;
                }
                else {
                    throw new InvalidDataException($"unknown evidence manifest header at line {lineNumber}");
                }

                continue;
            }

            var first = line.IndexOf('\t');
            var second = line.IndexOf('\t', first + 1);
            if (first <= 0 || second <= first) {
                throw new InvalidDataException($"malformed evidence manifest '{path}' at line {lineNumber}");
            }

            if (!long.TryParse(line[..first], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
                || !long.TryParse(line[(first + 1)..second], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var ticks)
                || size < 0) {
                throw new InvalidDataException($"invalid evidence metadata in '{path}' at line {lineNumber}");
            }

            var relative = line[(second + 1)..];
            if (relative.Length == 0 || !result.TryAdd(relative, (size, ticks))) {
                throw new InvalidDataException($"duplicate or empty evidence path in '{path}' at line {lineNumber}");
            }
        }

        return new(result, complete && headerSeen);
    }

    /// <summary>Concatenated semantic exports of the offline hives, headed per hive for diffing.</summary>
    private static async Task CaptureRegistryAsync(string mountPath, string outputFile, IProcessRunner runner,
        BuildLog log, CancellationToken ct) {
        var combined = new StringBuilder(1 << 20);
        foreach (var (hiveId, relativePath) in RegistryHiveCache.HiveFiles) {
            var hiveFile = Path.Combine(mountPath, relativePath.Replace('\\', Path.DirectorySeparatorChar));
            if (!File.Exists(hiveFile)) {
                continue;
            }

            var tempKey = $"HKLM\\TinyWin2Evidence_{Guid.NewGuid():N}";
            var exportFile = Path.GetTempFileName();
            var loaded = false;
            Exception? failure = null;
            try {
                loaded = true;
                await runner.RunAsync("reg.exe", ["load", tempKey, hiveFile], cancellationToken: ct);
                await runner.RunAsync("reg.exe", ["export", tempKey, exportFile, "/y"], cancellationToken: ct);
                var exported = await File.ReadAllTextAsync(exportFile, ct);
                combined.AppendLine($";;hive {hiveId}");
                combined.AppendLine(exported);
            }
            catch (Exception ex) {
                failure = ex;
            }
            finally {
                if (loaded) {
                    try {
                        await RegistryHiveCache.UnloadWithRetryAsync(runner, tempKey, hiveId, log,
                            CancellationToken.None);
                    }
                    catch (Exception ex) when (failure is not null) {
                        log.Error(
                            $"could not unload evidence hive '{hiveId}' after another snapshot failure: {ex.Message}");
                    }
                    catch (Exception ex) {
                        failure = ex;
                    }
                }

                try {
                    File.Delete(exportFile);
                }
                catch {
                    /* best effort */
                }
            }

            switch (failure) {
                case null:
                    continue;

                case OperationCanceledException:
                    ExceptionDispatchInfo.Capture(failure).Throw();
                    break;
            }

            throw new IOException($"could not snapshot hive '{hiveId}' for layer evidence", failure);
        }

        await File.WriteAllTextAsync(outputFile, combined.ToString(), ct);
    }

    /// <summary>Splits a combined registry snapshot back into per-hive .reg texts.</summary>
    public static Dictionary<string, string> SplitByHive(string snapshotText) {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var builder = new StringBuilder();
        foreach (var line in snapshotText.Split('\n')) {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(";;hive ", StringComparison.Ordinal)) {
                if (current is not null) {
                    result[current] = builder.ToString();
                }

                current = trimmed[";;hive ".Length..].Trim();
                builder = new StringBuilder();
                continue;
            }

            builder.AppendLine(trimmed);
        }

        if (current is not null) {
            result[current] = builder.ToString();
        }

        return result;
    }

    /// <summary>Jump-safe enumeration (reparse points recorded, never followed).</summary>
    private static bool Enumerate(string directory, string prefix, Action<long, DateTime, string> emit, BuildLog log,
        CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var complete = true;
        try {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory)) {
                ct.ThrowIfCancellationRequested();
                try {
                    var name = Path.GetFileName(entry);
                    var relative = prefix.Length == 0 ? name : $"{prefix}\\{name}";
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) {
                        emit(0, DateTime.MinValue, relative);
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0) {
                        complete &= Enumerate(entry, relative, emit, log, ct);
                    }
                    else {
                        // Skip offline-hive transaction leftovers (reg load/unload): they are
                        // build-machinery noise, not image content, and differ per layer otherwise.
                        if (HiveTransactionNoise().IsMatch(name)) {
                            continue;
                        }

                        var info = new FileInfo(entry);
                        emit(info.Length, info.LastWriteTimeUtc, relative);
                    }
                }
                catch (UnauthorizedAccessException ex) {
                    complete = false;
                    log.Warn($"skipping inaccessible evidence path '{entry}': {ex.Message}");
                }
                catch (IOException ex) {
                    complete = false;
                    log.Warn($"skipping unreadable evidence path '{entry}': {ex.Message}");
                }
            }
        }
        catch (UnauthorizedAccessException ex) {
            complete = false;
            log.Warn($"skipping inaccessible evidence directory '{directory}': {ex.Message}");
        }
        catch (IOException ex) {
            complete = false;
            log.Warn($"skipping unreadable evidence directory '{directory}': {ex.Message}");
        }

        return complete;
    }

    /// <summary>e.g. SOFTWARE{guid}.TM.blf / SOFTWARE{guid}.TMContainer….regtrans-ms / SOFTWARE.LOG1</summary>
    [GeneratedRegex(
        @"\{[0-9a-fA-F-]{36}\}\.TM(Container\d+)?(\.blf|\.regtrans-ms)$|^(SOFTWARE|SYSTEM|SECURITY|SAM|DEFAULT|NTUSER)\.LOG\d?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HiveTransactionNoise();
}
