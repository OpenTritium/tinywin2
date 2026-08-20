using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Pipeline;

public sealed record FileDiffEntry(string RelativePath, string Kind, long OldSize, long NewSize);

public sealed record RegistryDiffEntry(string Hive, string Key, string ValueName, string Kind, string? Before, string? After);

public sealed record LayerDiffReport(
    int FromIndex,
    int ToIndex,
    IReadOnlyList<FileDiffEntry> Files,
    IReadOnlyList<RegistryDiffEntry> Registry)
{
    public JsonObject ToJson() => new()
    {
        ["fromLayer"] = FromIndex,
        ["toLayer"] = ToIndex,
        ["files"] = new JsonArray(Files.Select(f => (JsonNode)new JsonObject
        {
            ["path"] = f.RelativePath,
            ["kind"] = f.Kind,
            ["oldSize"] = f.OldSize,
            ["newSize"] = f.NewSize,
        }).ToArray()),
        ["registry"] = new JsonArray(Registry.Select(r => (JsonNode)new JsonObject
        {
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
    IBuildLog log)
{
    /// <summary>Reads a layer's file tree without keeping it attached.</summary>
    private async Task<Dictionary<string, (long Size, DateTime WriteUtc)>> SnapshotTreeAsync(string vhdxPath, CancellationToken ct)
    {
        var letter = FreeDriveLetter();
        await backend.AttachAsync(vhdxPath, letter.ToString(), ct);
        try
        {
            var root = $"{letter}:\\";
            var snapshot = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                snapshot[info.FullName[(root.Length)..]] = (info.Length, info.LastWriteTimeUtc);
            }
            return snapshot;
        }
        finally
        {
            await backend.DetachAsync(vhdxPath, ct);
        }
    }

    public async Task<LayerDiffReport> DiffAsync(string workDirectory, int fromIndex, int toIndex, bool deep, CancellationToken ct)
    {
        var stack = VhdLayerStack.Load(workDirectory, backend, log);
        var fromVhdx = stack.VhdxForLayer(fromIndex);
        var toVhdx = stack.VhdxForLayer(toIndex);

        log.Info($"diffing layer {fromIndex:000} → {toIndex:000}");
        var before = await SnapshotTreeAsync(fromVhdx, ct);
        var after = await SnapshotTreeAsync(toVhdx, ct);

        var files = new List<FileDiffEntry>();
        foreach (var (path, _) in before.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!after.TryGetValue(path, out var newValue))
            {
                files.Add(new FileDiffEntry(path, "removed", before[path].Size, 0));
            }
            else if (newValue.Size != before[path].Size || newValue.WriteUtc != before[path].WriteUtc)
            {
                files.Add(new FileDiffEntry(path, "modified", before[path].Size, newValue.Size));
            }
        }
        foreach (var (path, value) in after.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!before.ContainsKey(path))
            {
                files.Add(new FileDiffEntry(path, "added", 0, value.Size));
            }
        }

        if (deep)
        {
            await HashVerifyAsync(files, fromVhdx, toVhdx, ct);
        }

        var registry = await DiffRegistryAsync(fromVhdx, toVhdx, ct);
        return new LayerDiffReport(fromIndex, toIndex, files, registry);
    }

    /// <summary>Second pass for same-size modified candidates: hash both copies.</summary>
    private async Task HashVerifyAsync(List<FileDiffEntry> files, string fromVhdx, string toVhdx, CancellationToken ct)
    {
        var candidates = files.Where(f => f.Kind == "modified").Select(f => f.RelativePath).ToList();
        if (candidates.Count == 0)
        {
            return;
        }
        var fromHashes = await HashFilesAsync(fromVhdx, candidates, ct);
        var toHashes = await HashFilesAsync(toVhdx, candidates, ct);
        files.RemoveAll(f => f.Kind == "modified"
                             && fromHashes.TryGetValue(f.RelativePath, out var a)
                             && toHashes.TryGetValue(f.RelativePath, out var b)
                             && a == b);
    }

    private async Task<Dictionary<string, string>> HashFilesAsync(string vhdxPath, List<string> paths, CancellationToken ct)
    {
        var letter = FreeDriveLetter();
        await backend.AttachAsync(vhdxPath, letter.ToString(), ct);
        try
        {
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var full = $"{letter}:\\{path}";
                if (File.Exists(full))
                {
                    hashes[path] = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(File.OpenRead(full), ct));
                }
            }
            return hashes;
        }
        finally
        {
            await backend.DetachAsync(vhdxPath, ct);
        }
    }

    private async Task<List<RegistryDiffEntry>> DiffRegistryAsync(string fromVhdx, string toVhdx, CancellationToken ct)
    {
        var result = new List<RegistryDiffEntry>();
        foreach (var (hiveId, relativePath) in RegistryHiveCache.HiveFiles)
        {
            var before = await ExportHiveAsync(fromVhdx, relativePath, ct);
            var after = await ExportHiveAsync(toVhdx, relativePath, ct);
            if (before is null && after is null)
            {
                continue;
            }
            if (before is null || after is null)
            {
                result.Add(new RegistryDiffEntry(hiveId, "(hive)", "(whole file)", before is null ? "added" : "removed", null, null));
                continue;
            }
            result.AddRange(RegTextDiff(hiveId, before, after));
        }
        return result;
    }

    /// <summary>Loads the hive (reg load) and exports it as .reg text; null when the hive file is absent.</summary>
    private async Task<string?> ExportHiveAsync(string vhdxPath, string hiveRelativePath, CancellationToken ct)
    {
        var letter = FreeDriveLetter();
        await backend.AttachAsync(vhdxPath, letter.ToString(), ct);
        try
        {
            var hiveFile = $"{letter}:\\{hiveRelativePath.Replace('\\', Path.DirectorySeparatorChar)}";
            if (!File.Exists(hiveFile))
            {
                return null;
            }
            var tempKey = $"HKLM\\TinyWin2Diff_{Guid.NewGuid():N}";
            var exportFile = Path.GetTempFileName();
            try
            {
                await runner.RunAsync("reg.exe", ["load", tempKey, hiveFile], cancellationToken: ct);
                try
                {
                    await runner.RunAsync("reg.exe", ["export", tempKey, exportFile, "/y"], cancellationToken: ct);
                }
                finally
                {
                    for (var attempt = 0; attempt < 5; attempt++)
                    {
                        var unload = await runner.RunAsync("reg.exe", ["unload", tempKey],
                            new ProcessRunOptions { IgnoreExitCode = true }, ct);
                        if (unload.ExitCode == 0)
                        {
                            break;
                        }
                        await Task.Delay(200, ct);
                    }
                }
                return File.Exists(exportFile) ? await File.ReadAllTextAsync(exportFile, ct) : null;
            }
            finally
            {
                try { File.Delete(exportFile); } catch { /* best effort */ }
            }
        }
        finally
        {
            await backend.DetachAsync(vhdxPath, ct);
        }
    }

    /// <summary>Parses .reg text into key→(name→raw value line) and diffs both sides.</summary>
    internal static List<RegistryDiffEntry> RegTextDiff(string hiveId, string beforeText, string afterText)
    {
        var before = ParseRegText(beforeText);
        var after = ParseRegText(afterText);
        var result = new List<RegistryDiffEntry>();

        foreach (var key in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            before.TryGetValue(key, out var oldValues);
            after.TryGetValue(key, out var newValues);
            oldValues ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            newValues ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in oldValues.Keys.Union(newValues.Keys))
            {
                oldValues.TryGetValue(name, out var oldValue);
                newValues.TryGetValue(name, out var newValue);
                if (oldValue == newValue)
                {
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
    internal static Dictionary<string, Dictionary<string, string>> ParseRegText(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }
            var header = KeyHeader().Match(line);
            if (header.Success)
            {
                var path = header.Groups[1].Value;
                // Strip the machine root plus the transient load key:
                // [HKEY_LOCAL_MACHINE\TinyWin2Diff_xxx\Rest\Of\Path] → Rest\Of\Path
                var firstSeparator = path.IndexOf('\\');
                var secondSeparator = firstSeparator < 0 ? -1 : path.IndexOf('\\', firstSeparator + 1);
                currentKey = secondSeparator > 0 ? path[(secondSeparator + 1)..] : path;
                if (!result.ContainsKey(currentKey))
                {
                    result[currentKey] = [];
                }
                continue;
            }
            if (currentKey is null)
            {
                continue;
            }
            if (line.StartsWith('@'))
            {
                result[currentKey]["(Default)"] = line;
            }
            else if (line.StartsWith('"'))
            {
                var closingQuote = FindClosingQuote(line);
                if (closingQuote > 0)
                {
                    result[currentKey][line[1..closingQuote]] = line;
                }
            }
        }
        return result;
    }

    private static int FindClosingQuote(string line)
    {
        for (var i = 1; i < line.Length; i++)
        {
            if (line[i] == '"' && line[i - 1] != '\\')
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Extracts one file (image-relative) from a layer to a host destination.</summary>
    public async Task ExtractAsync(string workDirectory, int layerIndex, string imageRelativePath, string destinationPath, CancellationToken ct)
    {
        var stack = VhdLayerStack.Load(workDirectory, backend, log);
        var vhdxPath = stack.VhdxForLayer(layerIndex);
        var letter = FreeDriveLetter();
        await backend.AttachAsync(vhdxPath, letter.ToString(), ct);
        try
        {
            var source = $"{letter}:\\{imageRelativePath.Replace('/', '\\')}";
            if (!File.Exists(source))
            {
                throw new FileNotFoundException($"'{imageRelativePath}' not found in layer {layerIndex:000}.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
            File.Copy(source, destinationPath, overwrite: true);
            log.Info($"extracted '{imageRelativePath}' from layer {layerIndex:000} → {destinationPath}");
        }
        finally
        {
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
        CancellationToken ct)
    {
        var stack = VhdLayerStack.Load(workDirectory, backend, log);
        var vhdxPath = stack.VhdxForLayer(layerIndex);
        var letter = FreeDriveLetter();
        await backend.AttachAsync(vhdxPath, letter.ToString(), ct);
        try
        {
            var name = $"TinyWin2 layer {layerIndex:000}";
            if (format == ImageFormat.Esd)
            {
                var intermediate = Path.Combine(Path.GetTempPath(), $"tinywin2-rollback-{Guid.NewGuid():N}.wim");
                try
                {
                    await new OutputBuilder(runner, log).CaptureAsync($"{letter}:\\", intermediate, name, null, ImageFormat.Wim, fast, ct);
                    await runner.RunAsync("dism.exe",
                        ["/Export-Image", $"/SourceImageFile:{intermediate}", "/SourceIndex:1",
                         $"/DestinationImageFile:{destinationPath}", "/Compress:recovery"], cancellationToken: ct);
                }
                finally
                {
                    try { File.Delete(intermediate); } catch { /* best effort */ }
                }
            }
            else
            {
                await new OutputBuilder(runner, log).CaptureAsync($"{letter}:\\", destinationPath, name, null, ImageFormat.Wim, fast, ct);
            }
            return destinationPath;
        }
        finally
        {
            await backend.DetachAsync(vhdxPath, ct);
        }
    }

    private static char FreeDriveLetter()
    {
        var letter = DiskPartVhdBackend.FreeDriveLetters().FirstOrDefault(l => l is >= 'S' and <= 'Z');
        return letter != default
            ? letter
            : throw new IOException("no free drive letter in S..Z for layer inspection");
    }
}
