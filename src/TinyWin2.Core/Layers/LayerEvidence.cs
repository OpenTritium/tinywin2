using System.Text;
using System.Text.RegularExpressions;
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
    public const string SnapshotsDirectoryName = "snapshots";

    public static string SnapshotsRoot(string workDirectory) => Path.Combine(workDirectory, SnapshotsDirectoryName);

    public static string ManifestPathFor(string snapshotsRoot, int index) => Path.Combine(snapshotsRoot, $"L{index:D3}.files.tsv");
    public static string RegistryPathFor(string snapshotsRoot, int index) => Path.Combine(snapshotsRoot, $"L{index:D3}.registry.reg");

    /// <summary>Captures evidence for one layer (index 0 = base). Must run while the layer is attached.</summary>
    public static async Task CaptureAsync(
        string mountPath,
        string workDirectory,
        int index,
        IProcessRunner runner,
        IBuildLog log,
        CancellationToken ct) {
        var snapshotsRoot = SnapshotsRoot(workDirectory);
        Directory.CreateDirectory(snapshotsRoot);
        await CaptureFileManifestAsync(mountPath, ManifestPathFor(snapshotsRoot, index), ct);
        await CaptureRegistryAsync(mountPath, RegistryPathFor(snapshotsRoot, index), runner, log, ct);
        log.Debug($"captured layer {index:000} evidence snapshots", layerIndex: index);
    }

    /// <summary>Jump-safe full-tree manifest: size \t mtimeUtc \t relativePath.</summary>
    private static async Task CaptureFileManifestAsync(string mountPath, string outputFile, CancellationToken ct) {
        var builder = new StringBuilder(1 << 20);
        var count = 0;
        Enumerate(mountPath.TrimEnd('\\') + "\\", "", (size, writeUtc, relative) => {
            builder.Append(size).Append('\t').Append(writeUtc.Ticks).Append('\t').Append(relative).Append('\n');
            count++;
        }, ct);
        await File.WriteAllTextAsync(outputFile, builder.ToString(), ct);
    }

    /// <summary>Loads a manifest snapshot: relativePath → (size, writeTicks).</summary>
    public static Dictionary<string, (long Size, long WriteTicks)> LoadManifest(string path) {
        var result = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) {
            return result;
        }
        foreach (var line in File.ReadLines(path)) {
            var first = line.IndexOf('\t');
            var second = line.IndexOf('\t', first + 1);
            if (first <= 0 || second <= first) {
                continue;
            }
            result[line[(second + 1)..]] = (long.Parse(line[..first]), long.Parse(line[(first + 1)..second]));
        }
        return result;
    }

    /// <summary>Concatenated semantic exports of the offline hives, headed per hive for diffing.</summary>
    private static async Task CaptureRegistryAsync(string mountPath, string outputFile, IProcessRunner runner, IBuildLog log, CancellationToken ct) {
        var combined = new StringBuilder(1 << 20);
        foreach (var (hiveId, relativePath) in RegistryHiveCache.HiveFiles) {
            var hiveFile = Path.Combine(mountPath, relativePath.Replace('\\', Path.DirectorySeparatorChar));
            if (!File.Exists(hiveFile)) {
                continue;
            }
            var tempKey = $"HKLM\\TinyWin2Evidence_{Guid.NewGuid():N}";
            var exportFile = Path.GetTempFileName();
            try {
                await runner.RunAsync("reg.exe", ["load", tempKey, hiveFile], cancellationToken: ct);
                try {
                    await runner.RunAsync("reg.exe", ["export", tempKey, exportFile, "/y"], cancellationToken: ct);
                }
                finally {
                    for (var attempt = 0; attempt < 5; attempt++) {
                        var unload = await runner.RunAsync("reg.exe", ["unload", tempKey],
                            new ProcessRunOptions { IgnoreExitCode = true }, ct);
                        if (unload.Success) {
                            break;
                        }
                        await Task.Delay(200, ct);
                    }
                }
                combined.AppendLine($";;hive {hiveId}");
                combined.AppendLine(await File.ReadAllTextAsync(exportFile, ct));
            }
            catch (Exception ex) {
                log.Warn($"could not snapshot hive '{hiveId}' for layer evidence: {ex.Message}");
            }
            finally {
                try { File.Delete(exportFile); } catch { /* best effort */ }
            }
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
    private static void Enumerate(string directory, string prefix, Action<long, DateTime, string> emit, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory)) {
            var name = Path.GetFileName(entry);
            var relative = prefix.Length == 0 ? name : $"{prefix}\\{name}";
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) {
                emit(0, DateTime.MinValue, relative);
                continue;
            }
            if ((attributes & FileAttributes.Directory) != 0) {
                Enumerate(entry, relative, emit, ct);
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
    }

    /// <summary>e.g. SOFTWARE{guid}.TM.blf / SOFTWARE{guid}.TMContainer….regtrans-ms / SOFTWARE.LOG1</summary>
    [GeneratedRegex(@"\{[0-9a-fA-F-]{36}\}\.TM(Container\d+)?(\.blf|\.regtrans-ms)$|^(SOFTWARE|SYSTEM|SECURITY|SAM|DEFAULT|NTUSER)\.LOG\d?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HiveTransactionNoise();
}
