using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Pipeline;

public sealed record FileDiffEntry(string RelativePath, string Kind, long OldSize, long NewSize);

public sealed record RegistryDiffEntry(
    string Hive,
    string Key,
    string ValueName,
    string Kind,
    string? Before,
    string? After);

public sealed record LayerDiffReport(
    int FromIndex,
    int ToIndex,
    IReadOnlyList<FileDiffEntry> Files,
    IReadOnlyList<RegistryDiffEntry> Registry) {
    public JsonObject ToJson() => new() {
        ["fromLayer"] = FromIndex,
        ["toLayer"] = ToIndex,
        ["files"] = new JsonArray(Files.Select(f => (JsonNode)new JsonObject {
            ["path"] = f.RelativePath,
            ["kind"] = f.Kind,
            ["oldSize"] = f.OldSize,
            ["newSize"] = f.NewSize,
        }).ToArray()),
        ["registry"] = new JsonArray(Registry.Select(r => (JsonNode)new JsonObject {
            ["hive"] = r.Hive,
            ["key"] = r.Key,
            ["name"] = r.ValueName,
            ["kind"] = r.Kind,
            ["before"] = r.Before,
            ["after"] = r.After,
        }).ToArray()),
    };
}

/// <summary>
/// Post-mortem tooling over a build's layer chain: list layers, diff two layers
/// (file tree + registry hives), extract files, capture output from an earlier layer.
/// </summary>
public sealed partial class LayerInspector(
    IProcessRunner runner,
    ILayerBackend backend,
    BuildLog log) {
    /// <summary>
    /// Diffs two layers via their commit-time evidence snapshots (file manifests + registry
    /// exports) — no VHDX re-attach needed, which some Windows builds reject after a build.
    /// </summary>
    public Task<LayerDiffReport> DiffAsync(string workDirectory, int fromIndex, int toIndex, CancellationToken ct) {
        var snapshotsRoot = Layers.LayerEvidence.SnapshotsRoot(workDirectory);
        var fromManifest = Layers.LayerEvidence.ManifestPathFor(snapshotsRoot, fromIndex);
        var toManifest = Layers.LayerEvidence.ManifestPathFor(snapshotsRoot, toIndex);
        if (!File.Exists(fromManifest) || !File.Exists(toManifest)) {
            throw new FileNotFoundException(
                $"layer evidence snapshots missing for {fromIndex:000}/{toIndex:000} under '{snapshotsRoot}' " +
                "(rebuild with a current engine version, which captures evidence at commit time).");
        }

        log.Info($"diffing layer {fromIndex:000} → {toIndex:000} via evidence snapshots");
        var before = LayerEvidence.LoadManifest(fromManifest);
        var after = LayerEvidence.LoadManifest(toManifest);
        var files = new List<FileDiffEntry>();
        foreach (var (path, oldEntry) in before.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) {
            if (!after.TryGetValue(path, out var newEntry)) {
                files.Add(new FileDiffEntry(path, "removed", oldEntry.Size, 0));
            }
            else if (newEntry.Size != oldEntry.Size || newEntry.WriteTicks != oldEntry.WriteTicks) {
                files.Add(new FileDiffEntry(path, "modified", oldEntry.Size, newEntry.Size));
            }
        }

        foreach (var (path, entry) in after.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) {
            if (!before.ContainsKey(path)) {
                files.Add(new FileDiffEntry(path, "added", 0, entry.Size));
            }
        }

        var registry = new List<RegistryDiffEntry>();
        var fromRegistryPath = Layers.LayerEvidence.RegistryPathFor(snapshotsRoot, fromIndex);
        var toRegistryPath = Layers.LayerEvidence.RegistryPathFor(snapshotsRoot, toIndex);
        if (File.Exists(fromRegistryPath) && File.Exists(toRegistryPath)) {
            var fromHives = Layers.LayerEvidence.SplitByHive(File.ReadAllText(fromRegistryPath));
            var toHives = Layers.LayerEvidence.SplitByHive(File.ReadAllText(toRegistryPath));
            foreach (var hiveId in fromHives.Keys.Union(toHives.Keys)) {
                fromHives.TryGetValue(hiveId, out var beforeText);
                toHives.TryGetValue(hiveId, out var afterText);
                if (beforeText is null || afterText is null) {
                    registry.Add(new RegistryDiffEntry(hiveId, "(hive)", "(whole file)",
                        beforeText is null ? "added" : "removed", null, null));
                    continue;
                }

                registry.AddRange(RegTextDiff(hiveId, beforeText, afterText));
            }
        }

        return Task.FromResult(new LayerDiffReport(fromIndex, toIndex, files, registry));
    }

    /// <summary>Parses .reg text into key→(name→raw value line) and diffs both sides.</summary>
    internal static List<RegistryDiffEntry> RegTextDiff(string hiveId, string beforeText, string afterText) {
        var before = ParseRegText(beforeText);
        var after = ParseRegText(afterText);
        var result = new List<RegistryDiffEntry>();
        foreach (var key in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) {
            before.TryGetValue(key, out var oldValues);
            after.TryGetValue(key, out var newValues);
            oldValues ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            newValues ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in oldValues.Keys.Union(newValues.Keys)) {
                oldValues.TryGetValue(name, out var oldValue);
                newValues.TryGetValue(name, out var newValue);
                if (oldValue == newValue) {
                    continue;
                }

                result.Add(new RegistryDiffEntry(
                    hiveId,
                    key,
                    name,
                    oldValue is null ? "added" : newValue is null ? "removed" : "modified",
                    oldValue,
                    newValue));
            }
        }

        return result;
    }

    [GeneratedRegex(@"^\[(.+)\]$")]
    private static partial Regex KeyHeader();

    /// <summary>.reg text → key path (root key stripped) → value name → raw value line.</summary>
    internal static Dictionary<string, Dictionary<string, string>> ParseRegText(string text) {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;
        foreach (var rawLine in text.Split('\n')) {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith(';')) {
                continue;
            }

            var header = KeyHeader().Match(line);
            if (header.Success) {
                var path = header.Groups[1].Value;
                // Strip the machine root plus the transient load key:
                // [HKEY_LOCAL_MACHINE\TinyWin2Diff_xxx\Rest\Of\Path] → Rest\Of\Path
                var firstSeparator = path.IndexOf('\\');
                var secondSeparator = firstSeparator < 0 ? -1 : path.IndexOf('\\', firstSeparator + 1);
                currentKey = secondSeparator > 0 ? path[(secondSeparator + 1)..] : path;
                if (!result.ContainsKey(currentKey)) {
                    result[currentKey] = [];
                }

                continue;
            }

            if (currentKey is null) {
                continue;
            }

            if (line.StartsWith('@')) {
                result[currentKey]["(Default)"] = line;
            }
            else if (line.StartsWith('"')) {
                var closingQuote = FindClosingQuote(line);
                if (closingQuote > 0) {
                    result[currentKey][line[1..closingQuote]] = line;
                }
            }
        }

        return result;
    }

    private static int FindClosingQuote(string line) {
        for (var i = 1; i < line.Length; i++) {
            if (line[i] == '"' && line[i - 1] != '\\') {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Extracts one file (image-relative) from a layer to a host destination.</summary>
    public async Task ExtractAsync(string workDirectory, int layerIndex, string imageRelativePath,
        string destinationPath, CancellationToken ct) {
        var stack = VhdLayerStack.Load(workDirectory, backend, log);
        var vhdxPath = stack.VhdxForLayer(layerIndex);
        var letter = await backend.AttachAsync(vhdxPath, ct);
        try {
            var source = $"{letter}:\\{imageRelativePath.Replace('/', '\\')}";
            if (!File.Exists(source)) {
                throw new FileNotFoundException($"'{imageRelativePath}' not found in layer {layerIndex:000}.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
            File.Copy(source, destinationPath, overwrite: true);
            log.Info($"extracted '{imageRelativePath}' from layer {layerIndex:000} → {destinationPath}");
        }
        finally {
            await backend.DetachAsync(vhdxPath, ct);
        }
    }

    /// <summary>Captures output from an earlier layer — the natural "rollback" of a diff chain.</summary>
    public async Task<string> RollbackCaptureAsync(
        string workDirectory,
        int layerIndex,
        string destinationPath,
        ImageFormat format,
        bool fast,
        CancellationToken ct) {
        var stack = VhdLayerStack.Load(workDirectory, backend, log);
        var vhdxPath = stack.VhdxForLayer(layerIndex);
        var letter = await backend.AttachAsync(vhdxPath, ct);
        try {
            var name = $"TinyWin2 layer {layerIndex:000}";
            if (format == ImageFormat.Esd) {
                var intermediate = Path.Combine(Path.GetTempPath(), $"tinywin2-rollback-{Guid.NewGuid():N}.wim");
                try {
                    await new OutputBuilder(runner, log).CaptureAsync($"{letter}:\\", intermediate, name, null,
                        ImageFormat.Wim, fast, ct);
                    await runner.RunAsync("dism.exe",
                    [
                        "/English", "/Export-Image", $"/SourceImageFile:{intermediate}", "/SourceIndex:1",
                        $"/DestinationImageFile:{destinationPath}", "/Compress:recovery"
                    ], cancellationToken: ct);
                }
                finally {
                    try {
                        File.Delete(intermediate);
                    }
                    catch {
                        /* best effort */
                    }
                }
            }
            else {
                await new OutputBuilder(runner, log).CaptureAsync($"{letter}:\\", destinationPath, name, null,
                    ImageFormat.Wim, fast, ct);
            }

            return destinationPath;
        }
        finally {
            await backend.DetachAsync(vhdxPath, ct);
        }
    }
}
